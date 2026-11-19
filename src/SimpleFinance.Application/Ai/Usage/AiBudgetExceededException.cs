using System.Globalization;

namespace SimpleFinance.Application.Ai.Usage;

/// <summary>
/// AI-FND-05: the typed degrade signal thrown by <see cref="IAiBudgetGuard"/> when a user's
/// month-to-date AI spend reaches their <c>AiMonthlyBudgetUsd</c> cap. A distinct exception type so
/// the provider clients (15-05) can <c>catch</c> it specifically and fall back (categorization →
/// rules-only, assistant → "orçamento de IA do mês atingido") without treating a normal, expected
/// over-budget condition as an unhandled failure. Carries the user, the current spend, and the cap
/// for messaging.
/// </summary>
public sealed class AiBudgetExceededException : Exception
{
    public Guid UserId { get; }

    public decimal CurrentSpendUsd { get; }

    public decimal MonthlyBudgetUsd { get; }

    public AiBudgetExceededException(Guid userId, decimal currentSpendUsd, decimal monthlyBudgetUsd)
        : base(string.Format(
            CultureInfo.InvariantCulture,
            "AI monthly budget of ${0} USD reached (spent ${1} USD this month) for user {2}.",
            monthlyBudgetUsd,
            currentSpendUsd,
            userId))
    {
        UserId = userId;
        CurrentSpendUsd = currentSpendUsd;
        MonthlyBudgetUsd = monthlyBudgetUsd;
    }
}
