using SimpleFinance.Infrastructure.Categorization;

namespace SimpleFinance.UnitTests.Categorization;

/// <summary>
/// Unit tests for <see cref="KnnCategoryVoter.ScoreNeighbors"/> (16-03 Task 1, AI-CAT-02 level 3).
/// The pure similarity-weighted vote math (16-RESEARCH §2) is exercised WITHOUT a database — the
/// pgvector query that feeds it is covered by the cascade/integration paths, but the confidence
/// formula and the proximity-floor gate are asserted here against the plan's fixtures.
/// </summary>
public class KnnCategoryVoterTests
{
    private const double Floor = 0.80;
    private static readonly Guid CatA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid CatB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void ScoreNeighbors_MajorityWithOneDissenter_WinsWithWeightedShare()
    {
        // (catA, 0.05), (catA, 0.10), (catB, 0.30)
        // sim = 1 - dist → 0.95, 0.90, 0.70 ; winner catA = (0.95+0.90)/(0.95+0.90+0.70) ≈ 0.725
        var neighbors = new List<(Guid, double)>
        {
            (CatA, 0.05),
            (CatA, 0.10),
            (CatB, 0.30),
        };

        var (winner, confidence) = KnnCategoryVoter.ScoreNeighbors(neighbors, Floor);

        Assert.Equal(CatA, winner);
        Assert.Equal(0.725, confidence, 3); // 1.85 / 2.55 ≈ 0.7255
    }

    [Fact]
    public void ScoreNeighbors_NearestBelowProximityFloor_ReturnsNoSignal()
    {
        // Nearest neighbour dist 0.30 → sim 0.70 < 0.80 floor: no close analogue exists yet.
        // Even though every neighbour agrees on catA, the floor gates the whole step → null.
        var neighbors = new List<(Guid, double)>
        {
            (CatA, 0.30),
            (CatA, 0.35),
            (CatA, 0.40),
        };

        var (winner, confidence) = KnnCategoryVoter.ScoreNeighbors(neighbors, Floor);

        Assert.Null(winner);
        Assert.Equal(0.0, confidence);
    }

    [Fact]
    public void ScoreNeighbors_UnanimousCloseNeighbors_ConfidenceIsOne()
    {
        var neighbors = new List<(Guid, double)>
        {
            (CatA, 0.01),
            (CatA, 0.03),
            (CatA, 0.05),
        };

        var (winner, confidence) = KnnCategoryVoter.ScoreNeighbors(neighbors, Floor);

        Assert.Equal(CatA, winner);
        Assert.Equal(1.0, confidence, 6);
    }

    [Fact]
    public void ScoreNeighbors_EmptyNeighborSet_ReturnsNoSignal()
    {
        var (winner, confidence) = KnnCategoryVoter.ScoreNeighbors(new List<(Guid, double)>(), Floor);

        Assert.Null(winner);
        Assert.Equal(0.0, confidence);
    }

    [Fact]
    public void ScoreNeighbors_NearestPassesFloor_EvenWithFarDissenters_StillVotes()
    {
        // The nearest neighbour (catA, 0.05 → sim 0.95) passes the floor, so the step fires; the far
        // catB neighbour still dilutes confidence but does not gate the vote.
        var neighbors = new List<(Guid, double)>
        {
            (CatA, 0.05),
            (CatB, 0.19),
        };

        var (winner, confidence) = KnnCategoryVoter.ScoreNeighbors(neighbors, Floor);

        Assert.Equal(CatA, winner);
        Assert.True(confidence is > 0.5 and < 1.0);
    }
}
