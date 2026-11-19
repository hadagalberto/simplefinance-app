using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleFinance.Infrastructure.Ai.Forecast;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Jobs;

/// <summary>
/// AI-FCT-01/02: the daily Hangfire job that computes and persists the current-month <see
/// cref="Domain.Entities.Forecast"/> for every opted-in user. For each user with <c>AiEnabled &amp;&amp;
/// AiForecastEnabled</c> it runs the deterministic <see cref="ForecastCalculator.ComputeAsync"/>, attaches
/// an optional <c>deepseek-chat</c> narrative via <see cref="ForecastNarrativeWriter.WriteAsync"/> (which
/// degrades to a null narrative when over budget), and REPLACE-upserts the row via
/// <see cref="ForecastService.UpsertAsync"/> — the <c>(UserId, Month)</c> row is overwritten each run so
/// the projection tracks the month as it progresses (never frozen at its day-1 value — RESEARCH Pitfall 1).
///
/// The deterministic numbers are built BEFORE any LLM call and persisted regardless of the narrative
/// outcome (grounding + degrade-safe): the model only ever supplies the optional prose. Registered as a
/// daily recurring job (<c>ai-forecast</c>) in Program.cs. Each user is processed inside its own try/catch +
/// <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker"/> clear so one user's failure
/// never aborts the batch (mirrors <see cref="AiInsightJob"/>).
/// </summary>
public sealed class AiForecastJob
{
    private readonly SimpleFinanceDbContext _db;
    private readonly ForecastCalculator _calculator;
    private readonly ForecastNarrativeWriter _narrator;
    private readonly ForecastService _service;
    private readonly ILogger<AiForecastJob> _logger;

    public AiForecastJob(
        SimpleFinanceDbContext db,
        ForecastCalculator calculator,
        ForecastNarrativeWriter narrator,
        ForecastService service,
        ILogger<AiForecastJob> logger)
    {
        _db = db;
        _calculator = calculator;
        _narrator = narrator;
        _service = service;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var referenceDate = DateTime.UtcNow;

        var users = await _db.UserSettings
            .Where(s => s.AiEnabled && s.AiForecastEnabled)
            .Select(s => new { s.UserId, s.AiChatModel })
            .ToListAsync(ct);

        foreach (var user in users)
        {
            try
            {
                await ProcessUserAsync(user.UserId, user.AiChatModel, referenceDate, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AiForecastJob failed for user {UserId}", user.UserId);
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task ProcessUserAsync(Guid userId, string chatModel, DateTime referenceDate, CancellationToken ct)
    {
        // Deterministic-first: the projection numbers are computed and owned here, never by the LLM.
        var forecast = await _calculator.ComputeAsync(userId, referenceDate, ct);
        forecast.UserId = userId;

        // Optional garnish — degrades to Narrative=null on AiBudgetExceededException / any failure; the
        // forecast still persists with its deterministic numbers below.
        await _narrator.WriteAsync(forecast, chatModel, ct);

        // REPLACE-per-month: overwrite the (UserId, Month) row so the daily run never freezes the projection.
        await _service.UpsertAsync(forecast, ct);
    }
}
