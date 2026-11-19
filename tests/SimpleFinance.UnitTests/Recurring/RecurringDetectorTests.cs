using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Recurring;

namespace SimpleFinance.UnitTests.Recurring;

/// <summary>
/// RECUR-01 (07-02-PLAN.md Task 2): proves RecurringDetector.DetectGroups's amount-tolerance +
/// cadence classification -- "the single trickiest heuristic in the whole product" per
/// research/FEATURES.md. Grouping key is always MerchantNormalizer.Normalize(MerchantName ??
/// Description), matching this plan's locked key_link.
/// </summary>
public class RecurringDetectorTests
{
    private static readonly DateTime Base = new(2026, 1, 1);

    private static readonly RecurringDetectionOptions DefaultOptions =
        new(MinOccurrences: 3, AmountTolerancePercent: 0.10m, WeeklyToleranceDays: 2,
            MonthlyMinDays: 25, MonthlyMaxDays: 35, YearlyToleranceDays: 15);

    private static RecurringTransactionInput Tx(
        string? merchantName, decimal amount, DateTime date, Guid? categoryId = null, string? description = null)
        => new(Guid.NewGuid(), date, amount, merchantName, description ?? merchantName ?? "N/A", categoryId);

    [Fact]
    public void DetectGroups_MonthlyCadenceStableAmount_ProducesOneMonthlyGroup()
    {
        var transactions = new[]
        {
            Tx("NETFLIX.COM", 100m, Base),
            Tx("NETFLIX.COM", 100m, Base.AddDays(30)),
            Tx("NETFLIX.COM", 100m, Base.AddDays(60)),
        };

        var result = RecurringDetector.DetectGroups(transactions, DefaultOptions);

        var group = Assert.Single(result);
        Assert.Equal(RecurringFrequency.Monthly, group.Frequency);
        Assert.Equal(3, group.OccurrenceCount);
    }

    [Fact]
    public void DetectGroups_BelowMinOccurrences_ProducesNoGroup()
    {
        var transactions = new[]
        {
            Tx("NETFLIX.COM", 100m, Base),
            Tx("NETFLIX.COM", 100m, Base.AddDays(30)),
        };

        var result = RecurringDetector.DetectGroups(transactions, DefaultOptions);

        Assert.Empty(result);
    }

    [Fact]
    public void DetectGroups_OutlierExcludedButRemainderQualifies_ProducesGroupWithReducedOccurrenceCount()
    {
        // Mean of all 4 = (100+100+100+125)/4 = 106.25; 10% tolerance band = [95.625, 116.875].
        // 125 falls outside that band and is excluded; the remaining 3 (100 each, 30-day gaps)
        // still satisfy MinOccurrences=3 and the monthly cadence window.
        var transactions = new[]
        {
            Tx("NETFLIX.COM", 100m, Base),
            Tx("NETFLIX.COM", 100m, Base.AddDays(30)),
            Tx("NETFLIX.COM", 100m, Base.AddDays(60)),
            Tx("NETFLIX.COM", 125m, Base.AddDays(90)),
        };

        var result = RecurringDetector.DetectGroups(transactions, DefaultOptions);

        var group = Assert.Single(result);
        Assert.Equal(3, group.OccurrenceCount);
        Assert.Equal(100m, group.AverageAmount);
        Assert.Equal(RecurringFrequency.Monthly, group.Frequency);
    }

    [Fact]
    public void DetectGroups_OutlierExclusionDropsBelowMinOccurrences_ProducesNoGroup()
    {
        var transactions = new[]
        {
            Tx("NETFLIX.COM", 100m, Base),
            Tx("NETFLIX.COM", 100m, Base.AddDays(30)),
            Tx("NETFLIX.COM", 100m, Base.AddDays(60)),
            Tx("NETFLIX.COM", 125m, Base.AddDays(90)),
        };
        var strictOptions = DefaultOptions with { MinOccurrences = 4 };

        var result = RecurringDetector.DetectGroups(transactions, strictOptions);

        Assert.Empty(result);
    }

    [Fact]
    public void DetectGroups_IrregularCadence_ProducesNoGroup()
    {
        // Gaps of 5, 40, 100 days -- average ~48.3, matching none of the weekly/monthly/yearly
        // windows even though the merchant name and amount are perfectly stable.
        var transactions = new[]
        {
            Tx("SOME MERCHANT", 50m, Base),
            Tx("SOME MERCHANT", 50m, Base.AddDays(5)),
            Tx("SOME MERCHANT", 50m, Base.AddDays(45)),
            Tx("SOME MERCHANT", 50m, Base.AddDays(145)),
        };

        var result = RecurringDetector.DetectGroups(transactions, DefaultOptions);

        Assert.Empty(result);
    }

    [Fact]
    public void DetectGroups_WeeklyCadence_ProducesWeeklyGroup()
    {
        // Gaps of 7, 8, 6 days -- average 7, within [7-2, 7+2].
        var transactions = new[]
        {
            Tx("GYM CO", 30m, Base),
            Tx("GYM CO", 30m, Base.AddDays(7)),
            Tx("GYM CO", 30m, Base.AddDays(15)),
            Tx("GYM CO", 30m, Base.AddDays(21)),
        };

        var result = RecurringDetector.DetectGroups(transactions, DefaultOptions);

        var group = Assert.Single(result);
        Assert.Equal(RecurringFrequency.Weekly, group.Frequency);
        Assert.Equal(4, group.OccurrenceCount);
    }

    [Fact]
    public void DetectGroups_YearlyCadence_ProducesYearlyGroup()
    {
        // Gaps of 360, 370 days -- average 365, within [365-15, 365+15].
        var transactions = new[]
        {
            Tx("ANNUAL INSURANCE CO", 500m, Base),
            Tx("ANNUAL INSURANCE CO", 500m, Base.AddDays(360)),
            Tx("ANNUAL INSURANCE CO", 500m, Base.AddDays(730)),
        };

        var result = RecurringDetector.DetectGroups(transactions, DefaultOptions);

        var group = Assert.Single(result);
        Assert.Equal(RecurringFrequency.Yearly, group.Frequency);
        Assert.Equal(3, group.OccurrenceCount);
    }

    [Fact]
    public void DetectGroups_TwoDistinctMerchants_ProducesTwoIndependentGroups()
    {
        var merchantATx = new[]
        {
            Tx("NETFLIX.COM", 100m, Base),
            Tx("NETFLIX.COM", 100m, Base.AddDays(30)),
            Tx("NETFLIX.COM", 100m, Base.AddDays(60)),
        };
        var merchantBTx = new[]
        {
            Tx("SPOTIFY AB", 40m, Base),
            Tx("SPOTIFY AB", 40m, Base.AddDays(30)),
            Tx("SPOTIFY AB", 40m, Base.AddDays(60)),
        };
        var transactions = merchantATx.Concat(merchantBTx).ToArray();

        var result = RecurringDetector.DetectGroups(transactions, DefaultOptions);

        Assert.Equal(2, result.Count);
        var groupA = Assert.Single(result, g => g.NormalizedMerchant == "NETFLIX.COM");
        var groupB = Assert.Single(result, g => g.NormalizedMerchant == "SPOTIFY AB");
        Assert.Equal(merchantATx.Select(t => t.Id).OrderBy(x => x), groupA.MemberTransactionIds.OrderBy(x => x));
        Assert.Equal(merchantBTx.Select(t => t.Id).OrderBy(x => x), groupB.MemberTransactionIds.OrderBy(x => x));
    }

    [Fact]
    public void DetectGroups_EmptyInput_ProducesEmptyResult()
    {
        var result = RecurringDetector.DetectGroups(Array.Empty<RecurringTransactionInput>(), DefaultOptions);

        Assert.Empty(result);
    }

    [Fact]
    public void DetectGroups_GroupsByNormalizedMerchantAcrossVaryingRawDescriptions_DisplayNameIsLatestRawValue()
    {
        // Two raw store-number variants of the same real-world merchant normalize to the same
        // key ("IFOOD RESTAURANTE"); the detector must group them together via
        // MerchantNormalizer.Normalize, and DisplayName must be the latest-dated raw value.
        var transactions = new[]
        {
            Tx("IFOOD *RESTAURANTE 111", 60m, Base),
            Tx("IFOOD *RESTAURANTE 222", 60m, Base.AddDays(30)),
            Tx("IFOOD *RESTAURANTE 333", 60m, Base.AddDays(60)),
        };

        var result = RecurringDetector.DetectGroups(transactions, DefaultOptions);

        var group = Assert.Single(result);
        Assert.Equal("IFOOD RESTAURANTE", group.NormalizedMerchant);
        Assert.Equal("IFOOD *RESTAURANTE 333", group.DisplayName);
    }

    [Fact]
    public void DetectGroups_CategoryId_MostFrequentNonNullWins()
    {
        var categoryA = Guid.NewGuid();
        var categoryB = Guid.NewGuid();
        var transactions = new[]
        {
            Tx("NETFLIX.COM", 100m, Base, categoryA),
            Tx("NETFLIX.COM", 100m, Base.AddDays(30), categoryA),
            Tx("NETFLIX.COM", 100m, Base.AddDays(60), categoryB),
        };

        var result = RecurringDetector.DetectGroups(transactions, DefaultOptions);

        var group = Assert.Single(result);
        Assert.Equal(categoryA, group.CategoryId);
    }

    [Fact]
    public void DetectGroups_CategoryId_TieBrokenByEarliestDate()
    {
        var earlierCategory = Guid.NewGuid();
        var laterCategory = Guid.NewGuid();
        var options = DefaultOptions with { MinOccurrences = 2 };
        var transactions = new[]
        {
            Tx("NETFLIX.COM", 100m, Base, earlierCategory),
            Tx("NETFLIX.COM", 100m, Base.AddDays(30), laterCategory),
        };

        var result = RecurringDetector.DetectGroups(transactions, options);

        var group = Assert.Single(result);
        Assert.Equal(earlierCategory, group.CategoryId);
    }

    [Fact]
    public void DetectGroups_CategoryId_AllNullYieldsNullCategoryId()
    {
        var transactions = new[]
        {
            Tx("NETFLIX.COM", 100m, Base),
            Tx("NETFLIX.COM", 100m, Base.AddDays(30)),
            Tx("NETFLIX.COM", 100m, Base.AddDays(60)),
        };

        var result = RecurringDetector.DetectGroups(transactions, DefaultOptions);

        var group = Assert.Single(result);
        Assert.Null(group.CategoryId);
    }

    [Fact]
    public void DetectGroups_ComputesAllResultFieldsCorrectly()
    {
        var category = Guid.NewGuid();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var transactions = new[]
        {
            new RecurringTransactionInput(id1, Base, 100m, "NETFLIX.COM", "NETFLIX.COM", category),
            new RecurringTransactionInput(id2, Base.AddDays(30), 100m, "NETFLIX.COM", "NETFLIX.COM", category),
            new RecurringTransactionInput(id3, Base.AddDays(60), 100m, "NETFLIX.COM", "NETFLIX.COM", category),
        };

        var result = RecurringDetector.DetectGroups(transactions, DefaultOptions);

        var group = Assert.Single(result);
        Assert.Equal("NETFLIX.COM", group.NormalizedMerchant);
        Assert.Equal("NETFLIX.COM", group.DisplayName);
        Assert.Equal(category, group.CategoryId);
        Assert.Equal(100m, group.AverageAmount);
        Assert.Equal(RecurringFrequency.Monthly, group.Frequency);
        Assert.Equal(Base, group.FirstSeenDate);
        Assert.Equal(Base.AddDays(60), group.LastSeenDate);
        Assert.Equal(Base.AddDays(60).AddMonths(1), group.NextExpectedDate);
        Assert.Equal(3, group.OccurrenceCount);
        Assert.Equal(
            new[] { id1, id2, id3 }.OrderBy(x => x),
            group.MemberTransactionIds.OrderBy(x => x));
    }
}
