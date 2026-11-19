using Microsoft.EntityFrameworkCore;
using SimpleFinance.Infrastructure.Persistence;
using ForecastEntity = SimpleFinance.Domain.Entities.Forecast;

namespace SimpleFinance.Infrastructure.Ai.Forecast;

/// <summary>
/// The UserId-scoped write/read spine for month forecasts (Phase 19, AI-FCT-01). The daily
/// <c>AiForecastJob</c> calls <see cref="UpsertAsync"/> to persist the current-month projection; the
/// forecast endpoint calls <see cref="GetAsync"/>. Deliberately in Infrastructure (not Application) — it
/// depends directly on <see cref="SimpleFinanceDbContext"/>, mirroring <c>AiInsightService</c>'s
/// placement, but with the opposite upsert semantics.
///
/// REPLACE, NOT SKIP (the anti-freeze rule, RESEARCH Pitfall 1): unlike <c>AiInsightService.UpsertAsync</c>
/// which no-ops when a <c>(UserId, DedupKey)</c> row already exists, <see cref="UpsertAsync"/> loads the
/// existing <c>(UserId, Month)</c> row and OVERWRITES its projection in place — so re-running the daily job
/// updates the current-month numbers rather than freezing them at the day-1 value. The unique
/// <c>(UserId, Month)</c> index guarantees exactly one row per user per month. Every query filters
/// <c>WHERE UserId == ...</c> (T-19-01) so a forecast never leaks/overwrites across users, and the global
/// soft-delete query filter excludes <c>DeletedAt != null</c> rows automatically.
/// </summary>
public sealed class ForecastService(SimpleFinanceDbContext db)
{
    /// <summary>
    /// Persist the given month forecast with REPLACE semantics keyed by <c>(UserId, Month)</c>: when no
    /// row exists for that user+month it is inserted; when one already exists its projection fields
    /// (income/expense/balance + range + <c>ByCategoryJson</c> + <c>Method</c> + <c>Narrative</c>) are
    /// overwritten in place and <c>UpdatedAt</c> is bumped — the daily job never freezes the projection.
    /// </summary>
    public async Task<ForecastEntity> UpsertAsync(ForecastEntity forecast, CancellationToken ct)
    {
        var existing = await db.Forecasts
            .FirstOrDefaultAsync(f => f.UserId == forecast.UserId && f.Month == forecast.Month, ct);

        if (existing is null)
        {
            db.Forecasts.Add(forecast);
            await db.SaveChangesAsync(ct);
            return forecast;
        }

        existing.ProjectedIncome = forecast.ProjectedIncome;
        existing.ProjectedExpense = forecast.ProjectedExpense;
        existing.ProjectedBalance = forecast.ProjectedBalance;
        existing.BalanceLow = forecast.BalanceLow;
        existing.BalanceHigh = forecast.BalanceHigh;
        existing.ByCategoryJson = forecast.ByCategoryJson;
        existing.Method = forecast.Method;
        existing.Narrative = forecast.Narrative;
        existing.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>
    /// The caller's forecast for the given month, or null when none exists. UserId-scoped — never returns
    /// another user's row.
    /// </summary>
    public async Task<ForecastEntity?> GetAsync(Guid userId, DateTime month, CancellationToken ct)
    {
        return await db.Forecasts
            .FirstOrDefaultAsync(f => f.UserId == userId && f.Month == month, ct);
    }
}
