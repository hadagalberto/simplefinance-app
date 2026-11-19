using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Recurring;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// The pure, deterministic statistical anomaly detector (18-04, AI-INS-02). Produces
/// <see cref="InsightSignal"/> candidates of <c>Kind=Anomaly</c> across four families — per-merchant
/// modified z-score (<see cref="ZScoreStats"/>), duplicate charge, subscription increase (reusing the
/// Phase 7 <see cref="RecurringTransaction"/> fields verbatim — never recomputed), and atypical category
/// spend (via <see cref="ICategorySpendReader"/>). Every read is UserId-scoped (T-18-12); all detectors
/// are scoped to <c>Type==Debit</c>, non-transfer, non-hidden, Posted transactions so income/salary is
/// never flagged (T-18-11); and the RecurringTransaction table is an implicit allowlist that excludes
/// legitimate recurring merchants from z-score/duplicate detection. Authoritative numbers live in
/// <c>DataJson</c>, a deterministic PT-BR fallback Title/Body is computed up front (persistence never
/// depends on the LLM), merchant free-text is redacted before it enters any surfaced string, and each
/// signal's per-occurrence DedupKey lets 18-05's job fire exactly once. No LLM, no persistence here.
/// </summary>
public sealed class AnomalyDetector(
    SimpleFinanceDbContext db,
    ICategorySpendReader categorySpend,
    IPiiRedactor redactor) : IAnomalyDetector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<InsightSignal>> DetectAsync(Guid userId, DateTime referenceDate, CancellationToken ct)
    {
        var (curStart, curEnd) = MonthBounds(referenceDate.Year, referenceDate.Month);
        var bucket = curStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var windowStart = referenceDate.AddMonths(-AnomalyThresholds.LookbackMonths);

        // Implicit allowlist = active recurring merchants (their variation is the subscription-increase
        // path, not a z-score/duplicate anomaly).
        var allowlist = (await db.RecurringTransactions
                .Where(r => r.UserId == userId && r.IsActive)
                .Select(r => r.NormalizedMerchant)
                .ToListAsync(ct))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet();

        // Debit-only, non-transfer, non-hidden, posted window (soft-delete filter is automatic). This
        // single scope kills the salary/income false-positive class.
        var raw = await db.Transactions
            .Where(t => t.UserId == userId
                && t.Type == TransactionType.Debit
                && !t.IsTransfer
                && !t.IsHidden
                && t.Status == TransactionStatus.Posted
                && t.Date >= windowStart && t.Date <= referenceDate)
            .Select(t => new { t.Id, t.MerchantName, t.Description, t.Amount, t.Date, t.RecurringTransactionId })
            .ToListAsync(ct);

        var candidates = raw
            .Select(t => new Expense(
                t.Id,
                MerchantNormalizer.Normalize(t.MerchantName ?? t.Description),
                t.MerchantName ?? t.Description,
                Math.Abs(t.Amount),
                t.Date,
                t.RecurringTransactionId))
            .Where(e => !string.IsNullOrEmpty(e.Merchant))
            // Exclude the recurring allowlist (by FK or by normalized-merchant match).
            .Where(e => e.RecurringTransactionId == null && !allowlist.Contains(e.Merchant))
            .ToList();

        var signals = new List<InsightSignal>();
        signals.AddRange(DetectZScore(candidates, curStart, curEnd, bucket));
        signals.AddRange(DetectDuplicates(candidates, curStart, curEnd, bucket));
        signals.AddRange(await DetectSubscriptionIncreaseAsync(userId, curStart, curEnd, bucket, ct));
        signals.AddRange(await DetectAtypicalCategoryAsync(userId, curStart, curEnd, bucket, ct));
        return signals;
    }

    // --- (a) per-merchant robust modified z-score ------------------------------------------------

    private IEnumerable<InsightSignal> DetectZScore(
        IReadOnlyList<Expense> candidates, DateTime curStart, DateTime curEnd, string bucket)
    {
        foreach (var group in candidates.GroupBy(e => e.Merchant))
        {
            var items = group.ToList();
            if (items.Count <= AnomalyThresholds.MinHistory)
            {
                continue; // leave-one-out history would be < MinHistory
            }

            foreach (var target in items)
            {
                var history = items.Where(x => x.Id != target.Id).Select(x => (double)x.Amount).ToList();
                var median = Median(history);

                // Only above-baseline charges are flag-worthy anomalies.
                if ((double)target.Amount <= median)
                {
                    continue;
                }

                var modZ = ZScoreStats.ModifiedZScore((double)target.Amount, history);
                if (modZ is not { } z || z <= AnomalyThresholds.ModZWarn)
                {
                    continue;
                }

                var severity = z > AnomalyThresholds.ModZCritical ? AiInsightSeverity.Critical : AiInsightSeverity.Warning;
                var merchant = redactor.Redact(target.Display);
                var dedupKey = $"Anomaly:zscore:{target.Id}";
                var zText = double.IsInfinity(z) ? "muito alto" : z.ToString("0.0", CultureInfo.InvariantCulture);

                var dataJson = JsonSerializer.Serialize(new
                {
                    kind = "Anomaly",
                    subkind = "zscore",
                    transactionId = target.Id,
                    merchant,
                    amount = target.Amount,
                    median = (decimal)median,
                    modifiedZScore = double.IsInfinity(z) ? (double?)null : z,
                    date = target.Date,
                    period = bucket,
                }, JsonOptions);

                yield return new InsightSignal(
                    dedupKey,
                    AiInsightKind.Anomaly,
                    severity,
                    merchant,
                    curStart,
                    curEnd,
                    dataJson,
                    dedupKey,
                    $"Gasto atípico em {merchant}",
                    $"Uma cobrança de {FormatMoney(target.Amount)} em {merchant} está muito acima do seu padrão de {FormatMoney((decimal)median)} para esse estabelecimento.");
            }
        }
    }

    // --- (b) duplicate charge (same merchant + amount within window) -----------------------------

    private IEnumerable<InsightSignal> DetectDuplicates(
        IReadOnlyList<Expense> candidates, DateTime curStart, DateTime curEnd, string bucket)
    {
        var window = TimeSpan.FromHours(AnomalyThresholds.DuplicateWindowHours);

        foreach (var group in candidates.GroupBy(e => (e.Merchant, Amount: Math.Round(e.Amount, 2))))
        {
            if (group.Key.Amount < AnomalyThresholds.DuplicateAmountFloor)
            {
                continue;
            }

            var ordered = group.OrderBy(e => e.Date).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var later = ordered[i];
                // The occurrence is the LATER charge; flag it if ANY earlier charge falls inside the window.
                var hasEarlierInWindow = ordered.Take(i).Any(earlier => later.Date - earlier.Date <= window);
                if (!hasEarlierInWindow)
                {
                    continue;
                }

                var severity = later.Amount >= AnomalyThresholds.DuplicateCriticalAmount
                    ? AiInsightSeverity.Critical
                    : AiInsightSeverity.Warning;
                var merchant = redactor.Redact(later.Display);
                var dedupKey = $"Anomaly:dup:{later.Id}";

                var dataJson = JsonSerializer.Serialize(new
                {
                    kind = "Anomaly",
                    subkind = "duplicate",
                    laterTransactionId = later.Id,
                    merchant,
                    amount = later.Amount,
                    date = later.Date,
                    windowHours = AnomalyThresholds.DuplicateWindowHours,
                    period = bucket,
                }, JsonOptions);

                yield return new InsightSignal(
                    dedupKey,
                    AiInsightKind.Anomaly,
                    severity,
                    merchant,
                    curStart,
                    curEnd,
                    dataJson,
                    dedupKey,
                    $"Cobrança duplicada em {merchant}",
                    $"Duas cobranças de {FormatMoney(later.Amount)} em {merchant} aparecem em menos de {AnomalyThresholds.DuplicateWindowHours}h — possível duplicidade.");
            }
        }
    }

    // --- (c) subscription increase (reuses RecurringTransaction, does NOT recompute) -------------

    private async Task<IEnumerable<InsightSignal>> DetectSubscriptionIncreaseAsync(
        Guid userId, DateTime curStart, DateTime curEnd, string bucket, CancellationToken ct)
    {
        var recs = await db.RecurringTransactions
            .Where(r => r.UserId == userId
                && r.IsActive
                && r.AmountIncreaseDetected
                && r.PreviousAverageAmount != null
                && r.PreviousAverageAmount > 0m)
            .ToListAsync(ct);

        var results = new List<InsightSignal>();
        foreach (var r in recs)
        {
            var prev = r.PreviousAverageAmount!.Value;
            var increasePct = (r.AverageAmount - prev) / prev;
            if (increasePct < AnomalyThresholds.SubIncreaseMinPct)
            {
                continue;
            }

            var severity = increasePct >= AnomalyThresholds.SubIncreaseCriticalPct
                ? AiInsightSeverity.Critical
                : AiInsightSeverity.Warning;
            var merchant = redactor.Redact(r.DisplayName);
            var newAvgText = r.AverageAmount.ToString("0.00", CultureInfo.InvariantCulture);
            var dedupKey = $"Anomaly:subincrease:{r.Id}:{newAvgText}";
            var pctText = FormatPct(increasePct);

            var dataJson = JsonSerializer.Serialize(new
            {
                kind = "Anomaly",
                subkind = "subincrease",
                recurringId = r.Id,
                merchant,
                previousAverage = prev,
                newAverage = r.AverageAmount,
                increasePct,
                period = bucket,
            }, JsonOptions);

            results.Add(new InsightSignal(
                dedupKey,
                AiInsightKind.Anomaly,
                severity,
                merchant,
                curStart,
                curEnd,
                dataJson,
                dedupKey,
                $"Assinatura {merchant} aumentou {pctText}%",
                $"A assinatura {merchant} subiu de {FormatMoney(prev)} para {FormatMoney(r.AverageAmount)} ({pctText}% a mais)."));
        }

        return results;
    }

    // --- (d) atypical category spend (current vs trailing 3-month avg, LARGE jumps only) ---------

    private async Task<IEnumerable<InsightSignal>> DetectAtypicalCategoryAsync(
        Guid userId, DateTime curStart, DateTime curEnd, string bucket, CancellationToken ct)
    {
        var current = await categorySpend.GetCategorySpendAsync(userId, curStart, curEnd, ct);

        var priorSum = new Dictionary<string, decimal>();
        for (var i = 1; i <= 3; i++)
        {
            var (ms, me) = MonthBounds2(curStart.AddMonths(-i));
            foreach (var row in await categorySpend.GetCategorySpendAsync(userId, ms, me, ct))
            {
                var key = CategoryKey(row.CategoryId);
                priorSum[key] = priorSum.GetValueOrDefault(key) + row.Amount;
            }
        }

        var results = new List<InsightSignal>();
        foreach (var row in current)
        {
            var key = CategoryKey(row.CategoryId);
            var baseline = priorSum.GetValueOrDefault(key) / 3m;
            var delta = row.Amount - baseline;

            if (baseline < AnomalyThresholds.CategoryAnomalyMinBaseline) continue;
            if (delta < AnomalyThresholds.CategoryAnomalyMinAbsolute) continue;

            var changePct = delta / baseline;
            if (changePct < AnomalyThresholds.CategoryAnomalyMinPct) continue;

            var severity = changePct >= AnomalyThresholds.CategoryAnomalyCriticalPct
                ? AiInsightSeverity.Critical
                : AiInsightSeverity.Warning;
            var subject = row.CategoryName ?? "Sem categoria";
            var subjectKey = row.CategoryId?.ToString() ?? "uncategorized";
            var dedupKey = $"Anomaly:category:{subjectKey}:{bucket}";
            var pctText = FormatPct(changePct);

            var dataJson = JsonSerializer.Serialize(new
            {
                kind = "Anomaly",
                subkind = "category",
                categoryId = row.CategoryId,
                category = subject,
                currentAmount = row.Amount,
                baselineAmount = baseline,
                changePct,
                period = bucket,
            }, JsonOptions);

            results.Add(new InsightSignal(
                dedupKey,
                AiInsightKind.Anomaly,
                severity,
                subject,
                curStart,
                curEnd,
                dataJson,
                dedupKey,
                $"Gasto atípico em {subject}",
                $"Você gastou {FormatMoney(row.Amount)} em {subject} este mês, {pctText}% acima da média de {FormatMoney(baseline)} dos últimos 3 meses."));
        }

        return results;
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static string CategoryKey(Guid? categoryId) => categoryId?.ToString() ?? "uncategorized";

    private static double Median(IReadOnlyList<double> xs)
    {
        var sorted = xs.OrderBy(x => x).ToArray();
        var n = sorted.Length;
        var mid = n / 2;
        return n % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static (DateTime start, DateTime end) MonthBounds(int year, int month)
    {
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        return (start, start.AddMonths(1).AddTicks(-1));
    }

    private static (DateTime start, DateTime end) MonthBounds2(DateTime anyDayInMonth) =>
        MonthBounds(anyDayInMonth.Year, anyDayInMonth.Month);

    private static string FormatMoney(decimal value) => "R$ " + value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatPct(decimal ratio) =>
        Math.Round(ratio * 100m, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private sealed record Expense(Guid Id, string Merchant, string Display, decimal Amount, DateTime Date, Guid? RecurringTransactionId);
}
