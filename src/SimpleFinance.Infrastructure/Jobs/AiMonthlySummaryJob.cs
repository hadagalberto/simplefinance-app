using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.Insights;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Jobs;

/// <summary>
/// AI-FCT-03/04: the day-1 Hangfire job that persists a <c>Kind=MonthlySummary</c> <see cref="AiInsight"/>
/// for the PRIOR month and pushes it exactly once. For every user with <c>AiEnabled &amp;&amp;
/// AiMonthlySummaryEnabled</c> it builds the deterministic <see cref="MonthlyFactPackage"/> via
/// <see cref="MonthlySummaryFactBuilder"/> (PII-redacted aggregates), assembles a deterministic FALLBACK
/// <see cref="AiInsight"/> (Title/Body grounded in the fact package) BEFORE any LLM call, asks
/// <see cref="MonthlySummarizer"/> (<c>deepseek-reasoner</c>, <see cref="AiFeature.MonthlySummary"/>) to
/// OVERWRITE the prose (degrading to the fallback on budget cap / any failure), and persists via
/// <see cref="AiInsightService.UpsertAsync"/>.
///
/// The unique <c>(UserId, DedupKey)</c> index is the fire-once + idempotency spine: the summary uses
/// SKIP-if-exists (DedupKey = <c>MonthlySummary:{yyyy-MM}</c>), so a second day-1 run for the same month
/// inserts nothing and — because the dispatch branch is only reached when <see cref="AiInsightService.UpsertAsync"/>
/// returned <c>true</c> — never re-dispatches (T-19-09). Dispatch is budget-INDEPENDENT: the deterministic
/// fallback body still pushes (T-19-10), exactly like <see cref="AiAnomalyJob"/>. Each user is processed
/// inside its own try/catch + <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker"/> clear
/// so one user's failure never aborts the batch. Registered as the monthly <c>ai-monthly-summary</c>
/// recurring job (<c>0 5 1 * *</c>) in Program.cs.
/// </summary>
public sealed class AiMonthlySummaryJob
{
    private readonly SimpleFinanceDbContext _db;
    private readonly MonthlySummaryFactBuilder _factBuilder;
    private readonly MonthlySummarizer _summarizer;
    private readonly AiInsightService _service;
    private readonly NotificationDispatcher _dispatcher;
    private readonly ILogger<AiMonthlySummaryJob> _logger;

    private static readonly string[] MonthNamesPtBr =
    [
        "janeiro", "fevereiro", "março", "abril", "maio", "junho",
        "julho", "agosto", "setembro", "outubro", "novembro", "dezembro",
    ];

    public AiMonthlySummaryJob(
        SimpleFinanceDbContext db,
        MonthlySummaryFactBuilder factBuilder,
        MonthlySummarizer summarizer,
        AiInsightService service,
        NotificationDispatcher dispatcher,
        ILogger<AiMonthlySummaryJob> logger)
    {
        _db = db;
        _factBuilder = factBuilder;
        _summarizer = summarizer;
        _service = service;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // The full month before the run instant (day-1 run → last month).
        var priorMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);

        var users = await _db.UserSettings
            .Where(s => s.AiEnabled && s.AiMonthlySummaryEnabled)
            .Select(s => new { s.UserId, s.AiSummaryModel })
            .ToListAsync(ct);

        foreach (var user in users)
        {
            try
            {
                await ProcessUserAsync(user.UserId, user.AiSummaryModel, priorMonthStart, now, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AiMonthlySummaryJob failed for user {UserId}", user.UserId);
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task ProcessUserAsync(Guid userId, string summaryModel, DateTime priorMonthStart, DateTime now, CancellationToken ct)
    {
        // Deterministic fact package (numbers never touch the LLM; merchant strings redacted at source).
        var package = await _factBuilder.BuildAsync(userId, priorMonthStart, ct);

        // Deterministic-first: build the fallback insight BEFORE the reasoner so an over-budget/failed run
        // still persists a grounded summary.
        var insight = BuildFallbackInsight(userId, package, now);

        // The ONE premium call — overwrites Title/Body on success, keeps the fallback otherwise. Never throws.
        await _summarizer.WriteAsync(insight, package.Json, summaryModel, ct);

        // SKIP-if-exists = idempotent once-per-month. false → already generated this month; the dispatch
        // branch below is unreachable, so no re-notify.
        var inserted = await _service.UpsertAsync(insight, ct);
        if (!inserted)
        {
            return;
        }

        var owner = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (owner is null)
        {
            return;
        }

        await FireAsync(userId, owner.Email, insight, ct);
    }

    /// <summary>
    /// Reproduces AlertEvaluationJob.FireAsync's shape: build the Notification (Type=AiMonthlySummary,
    /// Channel=Both), SaveChanges, then best-effort DispatchAsync (never throws). Reached ONLY on the run that
    /// just inserted the summary — so the notification row and the push are both fire-once via the DedupKey gate.
    /// </summary>
    private async Task FireAsync(Guid userId, string email, AiInsight insight, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = AlertRuleType.AiMonthlySummary,
            Title = insight.Title,
            Message = insight.Body,
            Channel = NotificationChannelType.Both,
            IsRead = false,
            SentAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        await _dispatcher.DispatchAsync(
            NotificationChannelType.Both, insight.Title, insight.Body, email, ct);
    }

    private static AiInsight BuildFallbackInsight(Guid userId, MonthlyFactPackage package, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Kind = AiInsightKind.MonthlySummary,
        Severity = AiInsightSeverity.Info,
        Title = BuildFallbackTitle(package),
        Body = BuildFallbackBody(package),
        PeriodStart = package.PeriodStart,
        PeriodEnd = package.PeriodEnd,
        DataJson = package.Json,
        DedupKey = $"MonthlySummary:{package.PeriodLabel}",
        Status = AiInsightStatus.New,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static string BuildFallbackTitle(MonthlyFactPackage package) =>
        $"Resumo de {MonthLabel(package.PeriodStart)}";

    private static string BuildFallbackBody(MonthlyFactPackage package)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"Receita R$ {package.Income:0.00}, despesa R$ {package.Expense:0.00}, saldo R$ {package.Balance:0.00}.");

        if (package.TopCategories.Count > 0)
        {
            var cats = string.Join(", ", package.TopCategories.Select(c =>
                string.Create(CultureInfo.InvariantCulture, $"{c.Name} (R$ {c.Amount:0.00})")));
            sb.Append(" Principais categorias: ").Append(cats).Append('.');
        }

        return sb.ToString();
    }

    private static string MonthLabel(DateTime monthStart) =>
        $"{MonthNamesPtBr[monthStart.Month - 1]}/{monthStart.Year.ToString(CultureInfo.InvariantCulture)}";
}
