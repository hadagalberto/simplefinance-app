using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;
using ForecastEntity = SimpleFinance.Domain.Entities.Forecast;

namespace SimpleFinance.Infrastructure.Ai.Forecast;

/// <summary>
/// The pure, deterministic month-forecast calculator (19-02, AI-FCT-01). Projects end-of-month
/// income/expense/balance + a variance range + per-category drivers by reusing the EXISTING reporting
/// reader bridges (<see cref="IPeriodTotalsReader.GetTotalsAsync"/> / <see cref="ICategorySpendReader"/>)
/// and <see cref="Domain.Entities.RecurringTransaction"/> fields verbatim — never a second, divergent
/// spend formula (the <c>InsightDetector</c> rule). The NUMBERS are authoritative and NEVER come from the
/// LLM; the daily job attaches an optional narrative afterwards.
///
/// Method (<c>recurring+3mo-avg</c>, all constants in <see cref="ForecastThresholds"/>, Phase-20-tunable):
/// <code>
/// realizedExpense   = GetTotals(monthStart..today).PeriodExpense
/// avg3moExpense     = mean(PeriodExpense of the 3 prior FULL months)
/// paceExpense       = realizedExpense / max(daysElapsed,1) * daysInMonth
/// upcomingRecurring = Σ active RecurringTransaction.AverageAmount WHERE NextExpectedDate in (today, monthEnd]
/// blend             = w*paceExpense + (1-w)*avg3moExpense
/// projectedExpense  = max(blend, realizedExpense + upcomingRecurring)                 // floor
/// projectedIncome   = max(realizedIncome, avg3moIncome)                               // recurring is Debit-only
/// projectedBalance  = projectedIncome − projectedExpense
/// band              = max(k*σ*(daysRemaining/daysInMonth), floorPct*projectedExpense) // σ over ≤6 prior months
/// BalanceHigh/Low   = projectedBalance ± band
/// </code>
/// The recurring filter <c>NextExpectedDate &gt; today</c> is the anti-double-count guard (RESEARCH
/// Pitfall 2): a charge already posted this month lives in <c>realizedExpense</c> and its recurring row's
/// <c>NextExpectedDate</c> has advanced into a future month, so it is never added twice. Income is derived
/// from history (Pitfall 3) — <c>RecurringTransaction</c> is Debit-only, there is no recurring income.
/// </summary>
public sealed class ForecastCalculator(
    IPeriodTotalsReader totals,
    ICategorySpendReader categorySpend,
    SimpleFinanceDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Build the fully-populated (deterministic) <see cref="ForecastEntity"/> for the month containing
    /// <paramref name="referenceDate"/>. <c>Month</c> is normalized to the 1st-of-month UTC; <c>Narrative</c>
    /// is left null (the job attaches it). Identical inputs always yield identical numbers.
    /// </summary>
    public async Task<ForecastEntity> ComputeAsync(Guid userId, DateTime referenceDate, CancellationToken ct)
    {
        var monthStart = new DateTime(referenceDate.Year, referenceDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEnd = monthStart.AddMonths(1).AddTicks(-1);
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);

        // Clamp the reference instant into the month so a stray timestamp can't skew daysElapsed / the
        // realized window. today is the upper bound of the "already realized" window.
        var today = referenceDate < monthStart ? monthStart : (referenceDate > monthEnd ? monthEnd : referenceDate);
        var daysElapsed = Math.Clamp(today.Day, 1, daysInMonth);
        var daysRemaining = daysInMonth - daysElapsed;

        // --- realized this month + trailing history (same aggregate the dashboard uses) ---
        var realized = await totals.GetTotalsAsync(userId, monthStart, today, ct);
        var realizedExpense = realized.PeriodExpense;
        var realizedIncome = realized.PeriodIncome;

        var priorExpenses = new List<decimal>();
        var priorIncomes = new List<decimal>();
        for (var i = 1; i <= ForecastThresholds.HistoryMonthsMax; i++)
        {
            var ms = monthStart.AddMonths(-i);
            var me = ms.AddMonths(1).AddTicks(-1);
            var t = await totals.GetTotalsAsync(userId, ms, me, ct);
            priorExpenses.Add(t.PeriodExpense);
            priorIncomes.Add(t.PeriodIncome);
        }

        var avg3moExpense = Mean(priorExpenses.Take(ForecastThresholds.BaselineMonths));
        var avg3moIncome = Mean(priorIncomes.Take(ForecastThresholds.BaselineMonths));

        // --- recurring debits still to come this month (anti-double-count via NextExpectedDate > today) ---
        var upcomingRecurring = await db.RecurringTransactions
            .Where(r => r.UserId == userId
                && r.IsActive
                && r.NextExpectedDate > today
                && r.NextExpectedDate <= monthEnd)
            .ToListAsync(ct);
        var upcomingRecurringTotal = upcomingRecurring.Sum(r => r.AverageAmount);

        // --- blend + floor ---
        var paceExpense = realizedExpense / Math.Max(daysElapsed, 1) * daysInMonth;
        var blend = ForecastThresholds.BlendWeight * paceExpense
            + (1m - ForecastThresholds.BlendWeight) * avg3moExpense;
        var projectedExpense = Round(Math.Max(blend, realizedExpense + upcomingRecurringTotal));

        var projectedIncome = Round(Math.Max(realizedIncome, avg3moIncome));
        var projectedBalance = projectedIncome - projectedExpense;

        // --- variance range (σ over up to 6 prior full months, min 3) ---
        var band = Round(ComputeBand(priorExpenses, daysRemaining, daysInMonth, projectedExpense));
        var balanceHigh = projectedBalance + band;
        var balanceLow = projectedBalance - band;

        // --- per-category drivers ---
        var byCategoryJson = await BuildByCategoryJsonAsync(
            userId, monthStart, monthEnd, today, daysElapsed, daysInMonth, projectedExpense,
            realizedIncome, realizedExpense, projectedIncome, projectedBalance, balanceLow, balanceHigh, ct);

        return new ForecastEntity
        {
            Month = monthStart,
            ProjectedIncome = projectedIncome,
            ProjectedExpense = projectedExpense,
            ProjectedBalance = projectedBalance,
            BalanceLow = balanceLow,
            BalanceHigh = balanceHigh,
            ByCategoryJson = byCategoryJson,
            Method = ForecastThresholds.Method,
            Narrative = null,
        };
    }

    private decimal ComputeBand(IReadOnlyList<decimal> priorExpenses, int daysRemaining, int daysInMonth, decimal projectedExpense)
    {
        var floor = ForecastThresholds.RangeFloorPct * projectedExpense;

        // Only months that actually carry expense count as history — a fetched-but-empty month (0, i.e. the
        // user had no data yet) is not a real "R$0 spent" observation and would otherwise inflate σ.
        var history = priorExpenses.Where(e => e > 0m).ToList();
        if (history.Count < ForecastThresholds.HistoryMonthsMin)
        {
            return floor;
        }

        var sigma = PopulationStdDev(history);
        var sigmaBand = ForecastThresholds.RangeSigmaK * sigma * ((decimal)daysRemaining / daysInMonth);
        return Math.Max(sigmaBand, floor);
    }

    private async Task<string> BuildByCategoryJsonAsync(
        Guid userId, DateTime monthStart, DateTime monthEnd, DateTime today, int daysElapsed, int daysInMonth,
        decimal projectedExpense, decimal realizedIncome, decimal realizedExpense, decimal projectedIncome,
        decimal projectedBalance, decimal balanceLow, decimal balanceHigh, CancellationToken ct)
    {
        // realized_c (current month) and baseline_c (mean of the 3 prior full months) per category — the
        // exact 3-prior-month loop InsightDetector uses, keyed by CategoryId (null => "uncategorized").
        var current = await categorySpend.GetCategorySpendAsync(userId, monthStart, today, ct);
        var realizedByCat = new Dictionary<string, decimal>();
        var nameByCat = new Dictionary<string, string?>();
        var idByCat = new Dictionary<string, Guid?>();
        foreach (var row in current)
        {
            var key = CategoryKey(row.CategoryId);
            realizedByCat[key] = realizedByCat.GetValueOrDefault(key) + row.Amount;
            nameByCat[key] = row.CategoryName;
            idByCat[key] = row.CategoryId;
        }

        var baselineSum = new Dictionary<string, decimal>();
        for (var i = 1; i <= ForecastThresholds.BaselineMonths; i++)
        {
            var ms = monthStart.AddMonths(-i);
            var me = ms.AddMonths(1).AddTicks(-1);
            foreach (var row in await categorySpend.GetCategorySpendAsync(userId, ms, me, ct))
            {
                var key = CategoryKey(row.CategoryId);
                baselineSum[key] = baselineSum.GetValueOrDefault(key) + row.Amount;
                nameByCat.TryAdd(key, row.CategoryName);
                idByCat.TryAdd(key, row.CategoryId);
            }
        }

        // recurring_c: active recurring debits dated in (today, monthEnd] grouped by category.
        var recurringByCat = new Dictionary<string, decimal>();
        var upcoming = await db.RecurringTransactions
            .Where(r => r.UserId == userId
                && r.IsActive
                && r.NextExpectedDate > today
                && r.NextExpectedDate <= monthEnd)
            .Select(r => new { r.CategoryId, r.AverageAmount })
            .ToListAsync(ct);
        foreach (var r in upcoming)
        {
            var key = CategoryKey(r.CategoryId);
            recurringByCat[key] = recurringByCat.GetValueOrDefault(key) + r.AverageAmount;
            idByCat.TryAdd(key, r.CategoryId);
        }

        // Resolve names for categories only seen via recurring (no spend row carried a name).
        var missingIds = idByCat
            .Where(kv => kv.Value.HasValue && string.IsNullOrEmpty(nameByCat.GetValueOrDefault(kv.Key)))
            .Select(kv => kv.Value!.Value)
            .Distinct()
            .ToList();
        if (missingIds.Count > 0)
        {
            var fetched = await db.Categories
                .Where(c => c.UserId == userId && missingIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(ct);
            foreach (var c in fetched)
            {
                var key = c.Id.ToString();
                if (string.IsNullOrEmpty(nameByCat.GetValueOrDefault(key)))
                {
                    nameByCat[key] = c.Name;
                }
            }
        }

        var keys = realizedByCat.Keys
            .Union(baselineSum.Keys)
            .Union(recurringByCat.Keys)
            .ToList();

        var drivers = new List<DriverRow>();
        foreach (var key in keys)
        {
            var realizedC = realizedByCat.GetValueOrDefault(key);
            var baselineC = baselineSum.GetValueOrDefault(key) / ForecastThresholds.BaselineMonths;
            var recurringC = recurringByCat.GetValueOrDefault(key);
            var pacedC = realizedC / Math.Max(daysElapsed, 1) * daysInMonth;
            var projectedC = Round(Math.Max(
                ForecastThresholds.BlendWeight * pacedC + (1m - ForecastThresholds.BlendWeight) * baselineC,
                realizedC + recurringC));

            drivers.Add(new DriverRow(
                idByCat.GetValueOrDefault(key),
                nameByCat.GetValueOrDefault(key) ?? "Sem categoria",
                Round(realizedC),
                Round(baselineC),
                Round(recurringC),
                projectedC,
                projectedExpense > 0m ? Math.Round(projectedC / projectedExpense, 4, MidpointRounding.AwayFromZero) : 0m));
        }

        var topDrivers = drivers
            .OrderByDescending(d => d.Projected)
            .Take(ForecastThresholds.TopDrivers)
            .Select(d => new
            {
                categoryId = d.CategoryId,
                category = d.Category,
                realized = d.Realized,
                baseline3mo = d.Baseline3mo,
                recurringUpcoming = d.RecurringUpcoming,
                projected = d.Projected,
                shareOfExpense = d.ShareOfExpense,
            })
            .ToList();

        var payload = new
        {
            method = ForecastThresholds.Method,
            generatedAt = today.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            reference = new { daysElapsed, daysInMonth },
            totals = new
            {
                projectedIncome,
                projectedExpense,
                projectedBalance,
                realizedIncome = Round(realizedIncome),
                realizedExpense = Round(realizedExpense),
                balanceLow,
                balanceHigh,
            },
            drivers = topDrivers,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string CategoryKey(Guid? categoryId) => categoryId?.ToString() ?? "uncategorized";

    private static decimal Mean(IEnumerable<decimal> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0m : list.Sum() / list.Count;
    }

    private static decimal PopulationStdDev(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return 0m;
        var mean = values.Sum() / values.Count;
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return (decimal)Math.Sqrt((double)variance);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed record DriverRow(
        Guid? CategoryId, string Category, decimal Realized, decimal Baseline3mo,
        decimal RecurringUpcoming, decimal Projected, decimal ShareOfExpense);
}
