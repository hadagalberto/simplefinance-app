using SimpleFinance.Infrastructure.Ai.Insights;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Behavior tests for <see cref="ZScoreStats"/> (18-04, AI-INS-02). Proves the robust modified
/// z-score: it returns null below the minimum history, flags a large outlier that the classic
/// mean/stddev z-score would MASK (documenting why the median/MAD variant is used), handles the
/// constant-baseline MAD==0 case with a relative fallback (never emits ∞ for a normal value), and
/// only ever produces a positive flag for above-baseline values.
/// </summary>
public class ZScoreStatsTests
{
    // A tight, realistic same-merchant history (6 points) plus a clear R$400 outlier.
    private static readonly double[] History = [100, 105, 98, 102, 110, 99];

    [Fact]
    public void ModifiedZScore_HistoryBelowMinimum_ReturnsNull()
    {
        // 5 points < MinHistory (6) → insufficient sample.
        double[] tooFew = [100, 105, 98, 102, 110];

        Assert.Null(ZScoreStats.ModifiedZScore(400, tooFew));
    }

    [Fact]
    public void ModifiedZScore_ClearOutlier_ExceedsCutoff()
    {
        var modZ = ZScoreStats.ModifiedZScore(400, History);

        Assert.NotNull(modZ);
        Assert.True(modZ > 3.5, $"expected modified z-score > 3.5 for a clear outlier, got {modZ}");
    }

    [Fact]
    public void ModifiedZScore_ClassicZScoreWouldMaskTheOutlier_ButModifiedFlagsIt()
    {
        // The whole point of the robust method: the outlier itself inflates the classic stddev so
        // its classic z-score lands BELOW 3.5 and never flags — while the modified z-score does.
        var sample = History.Append(400.0).ToArray();
        var mean = sample.Average();
        var variance = sample.Sum(x => (x - mean) * (x - mean)) / sample.Length;
        var stdDev = Math.Sqrt(variance);
        var classicZ = (400 - mean) / stdDev;

        Assert.True(classicZ < 3.5, $"classic z-score should MASK the outlier (<3.5), got {classicZ}");
        Assert.True(ZScoreStats.ModifiedZScore(400, History) > 3.5);
    }

    [Fact]
    public void ModifiedZScore_ConstantBaselineMadZero_AboveHalf_ReturnsPositiveInfinity()
    {
        double[] constant = [50, 50, 50, 50, 50, 50];

        // 80 > 50 * 1.5 (=75) → definite flag via the relative fallback.
        Assert.Equal(double.PositiveInfinity, ZScoreStats.ModifiedZScore(80, constant));
    }

    [Fact]
    public void ModifiedZScore_ConstantBaselineMadZero_WithinHalf_ReturnsZeroNotInfinity()
    {
        double[] constant = [50, 50, 50, 50, 50, 50];

        // 55 is NOT above 50 * 1.5 → 0, and crucially never ∞.
        var modZ = ZScoreStats.ModifiedZScore(55, constant);

        Assert.Equal(0, modZ);
        Assert.False(double.IsInfinity(modZ!.Value));
    }

    [Fact]
    public void ModifiedZScore_BelowBaselineValue_NeverProducesPositiveFlag()
    {
        // A smaller-than-usual charge is not a flag-worthy anomaly.
        var modZ = ZScoreStats.ModifiedZScore(50, History);

        Assert.NotNull(modZ);
        Assert.True(modZ <= 0, $"a below-median value must not yield a positive flag, got {modZ}");
        Assert.True(modZ < 3.5);
    }
}
