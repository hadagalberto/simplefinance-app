using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.Insights;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Jobs;

/// <summary>
/// Behavior tests for <see cref="AiMonthlySummaryJob"/> (19-03, AI-FCT-03/04). Runs on the EF InMemory
/// provider with a real <see cref="MonthlySummaryFactBuilder"/> over fixed-fact fakes, a fake
/// <see cref="ILlmClient"/>, and a COUNTING <see cref="INotificationChannel"/> inside a real
/// <see cref="NotificationDispatcher"/> so a dispatch is provable by call count. Proves the day-1 contract:
/// the prior-month summary persists once (idempotent per month — a second run inserts nothing and never
/// re-dispatches), the reasoner's <c>Content</c> is used while <c>ReasoningContent</c> (chain-of-thought) is
/// NEVER persisted, an over-budget reasoner still persists the deterministic fallback AND still dispatches
/// once, and the flag gate + per-user isolation hold.
/// </summary>
public class AiMonthlySummaryJobTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static UserSettings Settings(Guid userId, bool aiEnabled, bool summaryEnabled) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AiEnabled = aiEnabled,
        AiMonthlySummaryEnabled = summaryEnabled,
        AiSummaryModel = "deepseek-reasoner",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static User AppUser(Guid userId) => new()
    {
        Id = userId,
        Email = "owner@example.com",
        PasswordHash = "x",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static (AiMonthlySummaryJob Job, CountingChannel Channel) Build(SimpleFinanceDbContext db, ILlmClient llm)
    {
        var totals = new FixedTotals(income: 8000m, expense: 5000m);
        var builder = new MonthlySummaryFactBuilder(
            totals, new EmptyCategorySpend(), new EmptyBudgets(), new EmptyTopMerchants(), db, new PiiRedactor());
        var summarizer = new MonthlySummarizer(llm, NullLogger<MonthlySummarizer>.Instance);
        var service = new AiInsightService(db);
        var channel = new CountingChannel();
        var dispatcher = new NotificationDispatcher([channel], NullLogger<NotificationDispatcher>.Instance);
        var job = new AiMonthlySummaryJob(db, builder, summarizer, service, dispatcher, NullLogger<AiMonthlySummaryJob>.Instance);
        return (job, channel);
    }

    [Fact]
    public async Task ExecuteAsync_FirstRun_PersistsSummary_DispatchesOnce_SecondRunNoDupNoRedispatch()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, summaryEnabled: true));
        db.Users.Add(AppUser(userId));
        await db.SaveChangesAsync();

        var llm = new FakeLlm { Content = "{\"title\":\"Resumo do mês\",\"highlights\":[\"Gastou menos\"],\"suggestions\":[\"Continue assim\"]}" };

        var (job, channel) = Build(db, llm);
        await job.ExecuteAsync(CancellationToken.None);

        var insight = Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Equal(AiInsightKind.MonthlySummary, insight.Kind);
        Assert.StartsWith("MonthlySummary:", insight.DedupKey);
        var notification = Assert.Single(await db.Notifications.ToListAsync());
        Assert.Equal(AlertRuleType.AiMonthlySummary, notification.Type);
        Assert.Equal(NotificationChannelType.Both, notification.Channel);
        Assert.Equal(1, channel.SendAsyncCallCount);

        // Second run for the same month: Upsert returns false → no new insight, no re-dispatch.
        var (job2, channel2) = Build(db, llm);
        await job2.ExecuteAsync(CancellationToken.None);

        Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Single(await db.Notifications.ToListAsync());
        Assert.Equal(0, channel2.SendAsyncCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReasonerContentOnly_NeverPersistsReasoningContent()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, summaryEnabled: true));
        db.Users.Add(AppUser(userId));
        await db.SaveChangesAsync();

        const string secret = "SEGREDO_CHAIN_OF_THOUGHT";
        var llm = new FakeLlm
        {
            Content = "{\"title\":\"Título final\",\"highlights\":[\"destaque\"],\"suggestions\":[\"sugestão\"]}",
            ReasoningContent = secret,
        };

        var (job, _) = Build(db, llm);
        await job.ExecuteAsync(CancellationToken.None);

        var insight = Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Equal("Título final", insight.Title);
        Assert.DoesNotContain(secret, insight.Title);
        Assert.DoesNotContain(secret, insight.Body);
        Assert.DoesNotContain(secret, insight.DataJson);
    }

    [Fact]
    public async Task ExecuteAsync_OverBudget_DeterministicFallbackPersists_StillDispatchesOnce()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(Settings(userId, aiEnabled: true, summaryEnabled: true));
        db.Users.Add(AppUser(userId));
        await db.SaveChangesAsync();

        var llm = new FakeLlm { ThrowOnChat = new AiBudgetExceededException(userId, 5m, 5m) };

        var (job, channel) = Build(db, llm);
        await job.ExecuteAsync(CancellationToken.None);

        // Fallback Title/Body persist (grounded in the fact package); dispatch is budget-independent.
        var insight = Assert.Single(await db.AiInsights.ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(insight.Title));
        Assert.False(string.IsNullOrWhiteSpace(insight.Body));
        Assert.StartsWith("Resumo", insight.Title);
        Assert.Single(await db.Notifications.ToListAsync());
        Assert.Equal(1, channel.SendAsyncCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_FlagsOff_NeverProcessesUser()
    {
        await using var db = CreateDbContext();
        var aiOff = Guid.NewGuid();
        var summaryOff = Guid.NewGuid();
        db.UserSettings.Add(Settings(aiOff, aiEnabled: false, summaryEnabled: true));
        db.UserSettings.Add(Settings(summaryOff, aiEnabled: true, summaryEnabled: false));
        db.Users.Add(AppUser(aiOff));
        db.Users.Add(AppUser(summaryOff));
        await db.SaveChangesAsync();

        var llm = new FakeLlm { ThrowOnChat = new InvalidOperationException("LLM must not be called") };

        var (job, channel) = Build(db, llm);
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Empty(await db.AiInsights.ToListAsync());
        Assert.Empty(await db.Notifications.ToListAsync());
        Assert.Equal(0, channel.SendAsyncCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_OneUserThrows_OtherUsersStillProcessed()
    {
        await using var db = CreateDbContext();
        var badUser = Guid.NewGuid();
        var goodUser = Guid.NewGuid();
        db.UserSettings.Add(Settings(badUser, aiEnabled: true, summaryEnabled: true));
        db.UserSettings.Add(Settings(goodUser, aiEnabled: true, summaryEnabled: true));
        db.Users.Add(AppUser(goodUser)); // badUser has no User row → FireAsync path / lookups differ, but we force a throw via the builder
        await db.SaveChangesAsync();

        var llm = new FakeLlm { Content = "{\"title\":\"ok\",\"highlights\":[\"h\"],\"suggestions\":[\"s\"]}" };

        // A builder that throws only for badUser, isolating the failure.
        var totals = new ThrowForUserTotals(badUser, income: 8000m, expense: 5000m);
        var builder = new MonthlySummaryFactBuilder(
            totals, new EmptyCategorySpend(), new EmptyBudgets(), new EmptyTopMerchants(), db, new PiiRedactor());
        var summarizer = new MonthlySummarizer(llm, NullLogger<MonthlySummarizer>.Instance);
        var channel = new CountingChannel();
        var dispatcher = new NotificationDispatcher([channel], NullLogger<NotificationDispatcher>.Instance);
        var job = new AiMonthlySummaryJob(db, builder, summarizer, new AiInsightService(db), dispatcher, NullLogger<AiMonthlySummaryJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        var insight = Assert.Single(await db.AiInsights.ToListAsync());
        Assert.Equal(goodUser, insight.UserId);
    }

    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FixedTotals(decimal income, decimal expense) : IPeriodTotalsReader
    {
        public Task<DashboardTotals> GetTotalsAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct) =>
            Task.FromResult(new DashboardTotals(0m, 0m, 0m, income, expense));
    }

    private sealed class ThrowForUserTotals(Guid throwFor, decimal income, decimal expense) : IPeriodTotalsReader
    {
        public Task<DashboardTotals> GetTotalsAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            if (userId == throwFor)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(new DashboardTotals(0m, 0m, 0m, income, expense));
        }
    }

    private sealed class EmptyCategorySpend : ICategorySpendReader
    {
        public Task<IReadOnlyList<CategorySpend>> GetCategorySpendAsync(Guid userId, DateTime periodStart, DateTime periodEnd, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CategorySpend>>([]);
    }

    private sealed class EmptyBudgets : IBudgetReader
    {
        public Task<BudgetsResult> GetBudgetsForMonthAsync(Guid userId, int year, int month, CancellationToken ct) =>
            Task.FromResult(new BudgetsResult(year, month, false, []));
    }

    private sealed class EmptyTopMerchants : ITopMerchantsReader
    {
        public Task<IReadOnlyList<TopMerchantRow>> GetTopMerchantsAsync(Guid userId, DateTime periodStart, DateTime periodEnd, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TopMerchantRow>>([]);
    }

    private sealed class CountingChannel : INotificationChannel
    {
        public NotificationChannelType Type => NotificationChannelType.Telegram;

        public bool IsConfigured => true;

        public int SendAsyncCallCount { get; private set; }

        public Task SendAsync(string title, string message, string recipientEmail, CancellationToken ct)
        {
            SendAsyncCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLlm : ILlmClient
    {
        public string Content { get; set; } = "{}";

        public string? ReasoningContent { get; set; }

        public Exception? ThrowOnChat { get; set; }

        public Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct)
        {
            if (ThrowOnChat is not null)
            {
                throw ThrowOnChat;
            }

            return Task.FromResult(new LlmResult(Content, ReasoningContent, [], new TokenUsage(10, 5)));
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            throw new NotSupportedException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
