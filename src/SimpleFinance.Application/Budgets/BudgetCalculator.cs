namespace SimpleFinance.Application.Budgets;

/// <summary>
/// BUD-03's three budget states. Ok is below 80%, Attention spans the inclusive 80-100% range
/// (per BUD-03's literal "80-100%" wording -- exactly 100% is still Attention, not Exceeded),
/// Exceeded is strictly above 100%.
/// </summary>
public enum BudgetState
{
    Ok,
    Attention,
    Exceeded,
}

/// <summary>
/// The full derived-at-read-time budget picture for a single category (or the global cap) for one
/// month, per 05-02-PLAN.md's interfaces block. Never persisted -- Budget (05-01) stores only the
/// limit; every other field here is always recomputed live against current Transaction data so it
/// can never go stale.
/// </summary>
public sealed record BudgetProgress(
    decimal LimitAmount,
    decimal SpentAmount,
    decimal PercentOfCap,
    decimal Remaining,
    decimal ProjectedSpend,
    decimal ProjectedPercent,
    BudgetState State,
    bool IsAtRisk,
    bool IsProjectedToExceed);

/// <summary>
/// Pure, I/O-free budget math (05-02 Task 1) behind BUD-02/03/04. No DbContext, no I/O -- fully
/// unit-testable in isolation and reused as-is by 05-05's Budget endpoints and 05-06's Dashboard
/// at-risk-budgets endpoint so the threshold/projection formulas can never drift between callers.
/// </summary>
public static class BudgetCalculator
{
    public static BudgetProgress Calculate(decimal limitAmount, decimal spentAmount, int dayOfMonth, int daysInMonth)
    {
        // T-05-02: elapsedDays is clamped to at least 1 so a dayOfMonth of 0 (or any non-positive
        // value) can never trigger a DivideByZeroException in the projection below.
        var elapsedDays = Math.Max(1, dayOfMonth);

        var percentOfCap = CalculatePercent(limitAmount, spentAmount);
        var remaining = limitAmount - spentAmount;

        var projectedSpend = (spentAmount / elapsedDays) * daysInMonth;
        var projectedPercent = CalculatePercent(limitAmount, projectedSpend);

        var state = percentOfCap switch
        {
            > 100m => BudgetState.Exceeded,
            >= 80m => BudgetState.Attention,
            _ => BudgetState.Ok,
        };

        // IsAtRisk is intentionally just "State != Ok" -- Attention's 80-100% range already
        // strictly contains BUD-04's named 90% crossing point, so no separate parallel threshold
        // is needed here. Do not "fix" this into a redundant second comparison.
        var isAtRisk = state != BudgetState.Ok;
        var isProjectedToExceed = projectedPercent > 100m;

        return new BudgetProgress(
            limitAmount,
            spentAmount,
            percentOfCap,
            remaining,
            projectedSpend,
            projectedPercent,
            state,
            isAtRisk,
            isProjectedToExceed);
    }

    // Shared branching for both PercentOfCap and ProjectedPercent: a non-positive limit with any
    // positive spend is definitionally exceeded (101m cleanly forces the Exceeded branch without
    // dividing by zero); a non-positive limit with zero spend is 0%.
    private static decimal CalculatePercent(decimal limitAmount, decimal amount) =>
        limitAmount > 0m
            ? (amount / limitAmount) * 100m
            : (amount > 0m ? 101m : 0m);
}
