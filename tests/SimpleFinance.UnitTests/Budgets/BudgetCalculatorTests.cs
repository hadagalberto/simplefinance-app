using SimpleFinance.Application.Budgets;

namespace SimpleFinance.UnitTests.Budgets;

/// <summary>
/// Exercises BudgetCalculator (05-02 Task 1) -- a pure, I/O-free static method implementing
/// BUD-02 (spend/%/remaining/projection), BUD-03 (OK/Attention/Exceeded thresholds), and BUD-04's
/// detection half (at-risk/exceeded flag, no dispatch) exactly per 05-02-PLAN.md's interfaces
/// block and behavior list.
/// </summary>
public class BudgetCalculatorTests
{
    [Fact]
    public void Calculate_FiftyPercentSpent_MidMonth_IsOkAndNotAtRisk()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 1000m, spentAmount: 500m, dayOfMonth: 15, daysInMonth: 30);

        Assert.Equal(BudgetState.Ok, result.State);
        Assert.False(result.IsAtRisk);
    }

    [Fact]
    public void Calculate_SeventyNinePointNineNinePercent_IsOk()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 10000m, spentAmount: 7999m, dayOfMonth: 15, daysInMonth: 30);

        Assert.Equal(79.99m, result.PercentOfCap);
        Assert.Equal(BudgetState.Ok, result.State);
    }

    [Fact]
    public void Calculate_ExactlyEightyPercent_IsAttention_InclusiveLowerBoundary()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 1000m, spentAmount: 800m, dayOfMonth: 15, daysInMonth: 30);

        Assert.Equal(80m, result.PercentOfCap);
        Assert.Equal(BudgetState.Attention, result.State);
    }

    [Fact]
    public void Calculate_NinetyNinePercent_IsAttention()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 1000m, spentAmount: 990m, dayOfMonth: 15, daysInMonth: 30);

        Assert.Equal(99m, result.PercentOfCap);
        Assert.Equal(BudgetState.Attention, result.State);
    }

    [Fact]
    public void Calculate_ExactlyOneHundredPercent_IsAttention_NotExceeded()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 1000m, spentAmount: 1000m, dayOfMonth: 15, daysInMonth: 30);

        Assert.Equal(100m, result.PercentOfCap);
        Assert.Equal(BudgetState.Attention, result.State);
    }

    [Fact]
    public void Calculate_OneHundredPointZeroOnePercent_IsExceeded()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 10000m, spentAmount: 10001m, dayOfMonth: 15, daysInMonth: 30);

        Assert.Equal(100.01m, result.PercentOfCap);
        Assert.Equal(BudgetState.Exceeded, result.State);
    }

    [Fact]
    public void Calculate_ProjectionCrossesOneHundredPercent_WhileCurrentSpendStillOk_BothFlagsIndependent()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 1000m, spentAmount: 500m, dayOfMonth: 10, daysInMonth: 30);

        Assert.Equal(1500m, result.ProjectedSpend);
        Assert.Equal(150m, result.ProjectedPercent);
        Assert.True(result.IsProjectedToExceed);
        Assert.Equal(BudgetState.Ok, result.State);
        Assert.False(result.IsAtRisk);
    }

    [Fact]
    public void Calculate_NonPositiveLimitWithSpend_IsExceeded_NoException()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 0m, spentAmount: 50m, dayOfMonth: 15, daysInMonth: 30);

        Assert.Equal(BudgetState.Exceeded, result.State);
        Assert.Equal(101m, result.PercentOfCap);
    }

    [Fact]
    public void Calculate_DayOfMonthZero_ElapsedDaysClampedToOne_NoDivideByZero()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 1000m, spentAmount: 0m, dayOfMonth: 0, daysInMonth: 30);

        Assert.Equal(0m, result.ProjectedSpend);
    }

    [Fact]
    public void Calculate_ExceededSpend_RemainingGoesNegative_NeverClampedToZero()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 100m, spentAmount: 150m, dayOfMonth: 15, daysInMonth: 30);

        Assert.Equal(-50m, result.Remaining);
    }

    [Fact]
    public void Calculate_IsAtRisk_IsExactlyStateNotEqualToOk_ForAttention()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 1000m, spentAmount: 800m, dayOfMonth: 15, daysInMonth: 30);

        Assert.Equal(BudgetState.Attention, result.State);
        Assert.True(result.IsAtRisk);
    }

    [Fact]
    public void Calculate_IsAtRisk_IsExactlyStateNotEqualToOk_ForExceeded()
    {
        var result = BudgetCalculator.Calculate(limitAmount: 100m, spentAmount: 150m, dayOfMonth: 15, daysInMonth: 30);

        Assert.Equal(BudgetState.Exceeded, result.State);
        Assert.True(result.IsAtRisk);
    }
}
