using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.Usage;

/// <summary>
/// AI-FND-05: the hard monthly budget kill-switch (Denial-of-Wallet guardrail, T-15-07). Before any
/// cloud AI call the provider clients (15-05) invoke <see cref="EnsureWithinBudgetAsync"/>, which
/// sums the user's month-to-date <c>AiUsageLog.EstimatedCostUsd</c> and compares it to
/// <c>UserSettings.AiMonthlyBudgetUsd</c>. When the spend reaches the cap it throws
/// <see cref="AiBudgetExceededException"/> so callers degrade gracefully — a cap of 0 therefore
/// blocks the very first call (zero cloud calls, app fully functional). Deliberately in
/// Infrastructure (not Application) because it depends directly on <see cref="SimpleFinanceDbContext"/>,
/// mirroring <c>PreferencesService</c>'s documented rationale (Application cannot reference
/// Infrastructure without a circular project reference).
///
/// Month-to-date is a per-current-calendar-month (UTC) window, so the budget resets naturally on
/// rollover with no scheduled job. Uses <c>&gt;=</c> against the cap so a 0 budget trips on spend 0.
///
/// Notification dedup (T-15-09): the moment the cap is first crossed in a month, exactly one
/// <see cref="NotificationDispatcher"/> alert fires and a marker <see cref="Notification"/> row is
/// persisted; the per-month presence of that marker suppresses every later over-budget call's
/// dispatch until the next month naturally has no marker again.
/// </summary>
public sealed class AiBudgetGuard(SimpleFinanceDbContext db, NotificationDispatcher dispatcher) : IAiBudgetGuard
{
    // Stable title used BOTH as the user-facing alert heading AND as the per-month dedup marker key
    // (queried before dispatch). Must not change without considering in-flight monthly dedup.
    internal const string BudgetCapNotificationTitle = "Orçamento de IA do mês atingido";

    public async Task EnsureWithinBudgetAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextMonthStart = monthStart.AddMonths(1);

        var settings = await GetOrCreateAsync(userId, ct);
        var budget = settings.AiMonthlyBudgetUsd;

        // Month-to-date spend, UserId-scoped and bounded to the current UTC calendar month. EF's
        // global soft-delete query filter excludes DeletedAt rows automatically, and the LINQ is
        // fully parameterized (T-15-08). The (UserId, CreatedAt) index (15-03) covers this predicate.
        var spend = await db.AiUsageLogs
            .Where(l => l.UserId == userId
                && l.CreatedAt >= monthStart
                && l.CreatedAt < nextMonthStart)
            .SumAsync(l => l.EstimatedCostUsd, ct);

        if (spend < budget)
        {
            return;
        }

        await NotifyOncePerMonthAsync(userId, budget, spend, monthStart, nextMonthStart, now, ct);

        throw new AiBudgetExceededException(userId, spend, budget);
    }

    private async Task NotifyOncePerMonthAsync(
        Guid userId,
        decimal budget,
        decimal spend,
        DateTime monthStart,
        DateTime nextMonthStart,
        DateTime now,
        CancellationToken ct)
    {
        var alreadyNotified = await db.Notifications.AnyAsync(n =>
            n.UserId == userId
            && n.Title == BudgetCapNotificationTitle
            && n.CreatedAt >= monthStart
            && n.CreatedAt < nextMonthStart, ct);

        if (alreadyNotified)
        {
            return;
        }

        var message = string.Format(
            CultureInfo.InvariantCulture,
            "O limite mensal de gastos com IA (US$ {0:0.00}) foi atingido. Recursos de IA na nuvem "
            + "ficam pausados até o próximo mês; o app continua funcionando normalmente.",
            budget);

        // Persist the marker BEFORE dispatching so the dedup check above is authoritative even if a
        // second call races in the same month. Mirrors AlertEvaluationJob's persist-then-dispatch order.
        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = AlertRuleType.BudgetThreshold,
            Title = BudgetCapNotificationTitle,
            Message = message,
            Channel = NotificationChannelType.Both,
            IsRead = false,
            SentAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        // Single-user app: the recipient is the app's one User.Email (EmailChannel uses it; Telegram
        // ignores it). DispatchAsync never throws (08-02 contract), so it's safe to call inline.
        var email = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        await dispatcher.DispatchAsync(NotificationChannelType.Both, BudgetCapNotificationTitle, message, email, ct);
    }

    /// <summary>
    /// Get-or-create the user's settings row (PreferencesService.GetOrCreateAsync shape) so the cap
    /// is always readable — a first-ever call for a user with no persisted settings uses the entity's
    /// C# default AiMonthlyBudgetUsd. Idempotent.
    /// </summary>
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
}
