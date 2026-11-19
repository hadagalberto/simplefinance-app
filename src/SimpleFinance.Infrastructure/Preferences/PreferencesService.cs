using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Preferences;

/// <summary>
/// CFG-02's response shape -- exactly one row per user, so the frontend never needs to
/// reference it by id (no Id/UserId in the response, per 08-06-PLAN.md's interfaces block).
/// </summary>
public sealed record PreferencesResult(string Timezone, string BaseCurrency, string SyncCronExpression, string DefaultTheme);

/// <summary>
/// CFG-02: get-or-create preferences read + validated update, with a live Hangfire reschedule
/// side effect on the sync-cron field. Deliberately placed in Infrastructure (not Application),
/// mirroring BudgetService's documented rationale: it depends directly on
/// SimpleFinanceDbContext, and Application cannot reference Infrastructure without a circular
/// project reference. PreferencesEndpoints.cs is a thin HTTP wrapper over this class only -- no
/// business logic duplicated there.
/// </summary>
public sealed class PreferencesService(SimpleFinanceDbContext db, IHostEnvironment environment)
{
    /// <summary>
    /// Get-or-create read path (mirrors BudgetService.GetBudgetsForMonthAsync's copy-forward
    /// precedent): if no UserSettings row exists yet for userId, one is materialized with the
    /// entity's documented C# property defaults, persisted immediately, then returned -- a
    /// second call for the same userId always finds the row already present (idempotent, no
    /// re-insert).
    /// </summary>
    public async Task<PreferencesResult> GetPreferencesAsync(Guid userId, CancellationToken ct)
    {
        var settings = await GetOrCreateAsync(userId, ct);
        return ToResult(settings);
    }

    /// <summary>
    /// CFG-02's validated update path. Returns null (never throws) on any validation failure --
    /// mirrors BudgetService.UpsertBudgetAsync's established false/null-not-throw contract.
    /// Validation runs BEFORE any write, so an invalid value never mutates the stored row:
    /// (1) timezone via TimeZoneInfo.FindSystemTimeZoneById, (2) syncCronExpression via
    /// <see cref="IsValidCronExpression"/> -- Hangfire.Core 1.8.23 ILRepacks its underlying
    /// Cronos parser as an internal type (Cronos.CronExpression is not publicly accessible from
    /// this assembly, confirmed at implementation time), so this is a standalone standard
    /// 5-field cron structural validator (minute/hour/day-of-month/month/day-of-week, each
    /// accepting *, N, N-M, N,M, and */S step syntax) that accepts exactly the same expressions
    /// Hangfire's RecurringJob.AddOrUpdate call below will schedule, (3) defaultTheme must be
    /// exactly "light" or "dark" (case-sensitive, matches frontend/src/hooks/use-theme.ts's
    /// Theme union literally). baseCurrency has no format validation -- CFG-02's decision:
    /// display-only, any value is accepted since nothing downstream parses it.
    /// </summary>
    public async Task<PreferencesResult?> UpdatePreferencesAsync(
        Guid userId, string timezone, string baseCurrency, string syncCronExpression, string defaultTheme, CancellationToken ct)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }

        if (!IsValidCronExpression(syncCronExpression))
        {
            return null;
        }

        if (defaultTheme is not ("light" or "dark"))
        {
            return null;
        }

        var settings = await GetOrCreateAsync(userId, ct);
        settings.Timezone = timezone;
        settings.BaseCurrency = baseCurrency;
        settings.SyncCronExpression = syncCronExpression;
        settings.DefaultTheme = defaultTheme;
        settings.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        // Skipped under "Testing" -- Hangfire.PostgreSql storage doesn't work against EF Core's
        // InMemory provider used by WebApplicationFactory-based tests (mirrors every other
        // Hangfire-touching code path in this codebase, e.g. Program.cs's AddHangfire guard).
        // This is the load-bearing side effect that makes "sync times" a real, functioning
        // preference: the LIVE Hangfire recurring job's schedule actually changes, not just the
        // database row.
        if (!environment.IsEnvironment("Testing"))
        {
            RecurringJob.AddOrUpdate<DailySyncJob>(
                "daily-sync",
                job => job.ExecuteAsync(CancellationToken.None),
                syncCronExpression);
        }

        return ToResult(settings);
    }

    private async Task<UserSettings> GetOrCreateAsync(Guid userId, CancellationToken ct)
    {
        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (settings is not null)
        {
            return settings;
        }

        var now = DateTime.UtcNow;
        settings = new UserSettings
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.UserSettings.Add(settings);
        await db.SaveChangesAsync(ct);

        return settings;
    }

    private static PreferencesResult ToResult(UserSettings settings) =>
        new(settings.Timezone, settings.BaseCurrency, settings.SyncCronExpression, settings.DefaultTheme);

    // Standard 5-field cron structural validator (minute hour day-of-month month day-of-week).
    // See UpdatePreferencesAsync's doc-comment for why this is standalone rather than delegating
    // to Hangfire's internal Cronos parser.
    private static bool IsValidCronExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            return false;
        }

        return IsValidCronField(fields[0], 0, 59)
            && IsValidCronField(fields[1], 0, 23)
            && IsValidCronField(fields[2], 1, 31)
            && IsValidCronField(fields[3], 1, 12)
            && IsValidCronField(fields[4], 0, 7);
    }

    private static bool IsValidCronField(string field, int min, int max)
    {
        var parts = field.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(part => IsValidCronFieldPart(part, min, max));
    }

    private static bool IsValidCronFieldPart(string part, int min, int max)
    {
        var stepSplit = part.Split('/');
        if (stepSplit.Length > 2)
        {
            return false;
        }

        if (stepSplit.Length == 2 && (!int.TryParse(stepSplit[1], out var step) || step <= 0))
        {
            return false;
        }

        var basePart = stepSplit[0];
        if (basePart == "*")
        {
            return true;
        }

        var rangeSplit = basePart.Split('-');
        return rangeSplit.Length switch
        {
            1 => int.TryParse(rangeSplit[0], out var value) && value >= min && value <= max,
            2 => int.TryParse(rangeSplit[0], out var start) && int.TryParse(rangeSplit[1], out var end)
                && start >= min && end <= max && start <= end,
            _ => false,
        };
    }
}
