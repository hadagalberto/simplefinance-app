using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Recurring;

/// <summary>
/// RECUR-01 (07-02-PLAN.md): config-backed thresholds driving <see cref="RecurringDetector"/>'s
/// cadence/amount-tolerance classification. No defaults baked in here -- the caller (07-03's job)
/// supplies real values via config binding (07-CONTEXT.md example defaults: MinOccurrences=3,
/// AmountTolerancePercent=0.10m, WeeklyToleranceDays=2, MonthlyMinDays=25, MonthlyMaxDays=35,
/// YearlyToleranceDays=15).
/// </summary>
public sealed record RecurringDetectionOptions(
    int MinOccurrences,
    decimal AmountTolerancePercent,
    int WeeklyToleranceDays,
    int MonthlyMinDays,
    int MonthlyMaxDays,
    int YearlyToleranceDays);

/// <summary>
/// A single already-filtered transaction fed into <see cref="RecurringDetector.DetectGroups"/>.
/// <see cref="Amount"/> is always the already-Math.Abs'd magnitude; the caller is responsible for
/// filtering to POSTED/non-transfer/non-hidden/Debit rows before calling this, exactly like every
/// other Phase 5/6 aggregate's documented exclusion discipline.
/// </summary>
public sealed record RecurringTransactionInput(
    Guid Id,
    DateTime Date,
    decimal Amount,
    string? MerchantName,
    string Description,
    Guid? CategoryId);

/// <summary>A merchant group <see cref="RecurringDetector.DetectGroups"/> classified as recurring.</summary>
public sealed record RecurringGroupResult(
    string NormalizedMerchant,
    string DisplayName,
    Guid? CategoryId,
    decimal AverageAmount,
    RecurringFrequency Frequency,
    DateTime FirstSeenDate,
    DateTime LastSeenDate,
    DateTime NextExpectedDate,
    int OccurrenceCount,
    IReadOnlyList<Guid> MemberTransactionIds);

/// <summary>
/// RECUR-01 (07-02-PLAN.md Task 2): pure cadence/amount-tolerance classification -- "the single
/// trickiest heuristic in the whole product" per research/FEATURES.md. Groups transactions by
/// <see cref="MerchantNormalizer.Normalize"/>, keeps only members within an amount-tolerance band
/// of the group mean, and promotes the in-tolerance subset to a <see cref="RecurringGroupResult"/>
/// only when it meets both the minimum-occurrence count and a regular weekly/monthly/yearly
/// cadence window. No database, no Hangfire, no HTTP -- 07-03's RecurringDetectionJob calls this
/// directly.
/// </summary>
public static class RecurringDetector
{
    public static IReadOnlyList<RecurringGroupResult> DetectGroups(
        IReadOnlyList<RecurringTransactionInput> transactions, RecurringDetectionOptions options)
    {
        var results = new List<RecurringGroupResult>();

        var merchantGroups = transactions
            .GroupBy(t => MerchantNormalizer.Normalize(t.MerchantName ?? t.Description));

        foreach (var merchantGroup in merchantGroups)
        {
            var members = merchantGroup.ToList();

            // T-07-03: bounded, realistic per-merchant occurrence counts (accepted risk) -- no
            // pathological input size guarding needed here.
            var overallMean = members.Average(t => t.Amount);
            var tolerance = overallMean * options.AmountTolerancePercent;

            var inTolerance = members
                .Where(t => Math.Abs(t.Amount - overallMean) <= tolerance)
                .OrderBy(t => t.Date)
                .ToList();

            if (inTolerance.Count < options.MinOccurrences || inTolerance.Count < 2)
            {
                continue;
            }

            var gaps = new List<double>();
            for (var i = 1; i < inTolerance.Count; i++)
            {
                gaps.Add((inTolerance[i].Date - inTolerance[i - 1].Date).TotalDays);
            }
            var averageGapDays = gaps.Average();

            var frequency = ClassifyFrequency(averageGapDays, options);
            if (frequency is null)
            {
                continue;
            }

            var firstSeenDate = inTolerance[0].Date;
            var lastSeenDate = inTolerance[^1].Date;
            var nextExpectedDate = frequency switch
            {
                RecurringFrequency.Weekly => lastSeenDate.AddDays(7),
                RecurringFrequency.Monthly => lastSeenDate.AddMonths(1),
                RecurringFrequency.Yearly => lastSeenDate.AddYears(1),
                _ => lastSeenDate,
            };

            var latestMember = inTolerance.OrderByDescending(t => t.Date).First();
            var displayName = latestMember.MerchantName ?? latestMember.Description;

            var categoryId = ResolveCategoryId(inTolerance);

            results.Add(new RecurringGroupResult(
                NormalizedMerchant: merchantGroup.Key,
                DisplayName: displayName,
                CategoryId: categoryId,
                AverageAmount: inTolerance.Average(t => t.Amount),
                Frequency: frequency.Value,
                FirstSeenDate: firstSeenDate,
                LastSeenDate: lastSeenDate,
                NextExpectedDate: nextExpectedDate,
                OccurrenceCount: inTolerance.Count,
                MemberTransactionIds: inTolerance.Select(t => t.Id).ToList()));
        }

        return results;
    }

    private static RecurringFrequency? ClassifyFrequency(double averageGapDays, RecurringDetectionOptions options)
    {
        if (averageGapDays >= 7 - options.WeeklyToleranceDays && averageGapDays <= 7 + options.WeeklyToleranceDays)
        {
            return RecurringFrequency.Weekly;
        }

        if (averageGapDays >= options.MonthlyMinDays && averageGapDays <= options.MonthlyMaxDays)
        {
            return RecurringFrequency.Monthly;
        }

        if (averageGapDays >= 365 - options.YearlyToleranceDays && averageGapDays <= 365 + options.YearlyToleranceDays)
        {
            return RecurringFrequency.Yearly;
        }

        return null;
    }

    private static Guid? ResolveCategoryId(IReadOnlyList<RecurringTransactionInput> inTolerance)
    {
        var nonNullCategories = inTolerance.Where(t => t.CategoryId is not null).ToList();
        if (nonNullCategories.Count == 0)
        {
            return null;
        }

        return nonNullCategories
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, Count = g.Count(), EarliestDate = g.Min(t => t.Date) })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.EarliestDate)
            .First()
            .CategoryId;
    }
}
