using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.NetWorth;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Recurring;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.Infrastructure.Ai.Insights;

/// <summary>
/// The pure, deterministic insight-signal detector (18-02, AI-INS-01). Reuses the EXISTING reporting
/// reader bridges (<see cref="ICategorySpendReader"/> / <see cref="IBudgetReader"/> /
/// <see cref="INetWorthReader"/>) and <see cref="Domain.Entities.RecurringTransaction"/> fields verbatim —
/// never a second, divergent spend/budget formula — plus a UserId-scoped transaction scan for the
/// new-merchant fold. Every candidate carries its authoritative numbers in <c>DataJson</c>, a deterministic
/// PT-BR fallback Title/Body, a Severity, and a composed DedupKey. Merchant free-text is passed through the
/// <see cref="IPiiRedactor"/> before it reaches any surfaced string (T-18-05). No LLM, no persistence: the
/// caller feeds the returned list to 18-03's prose+Upsert layer.
/// </summary>
public sealed class InsightDetector(
    ICategorySpendReader categorySpend,
    IBudgetReader budgets,
    INetWorthReader netWorth,
    SimpleFinanceDbContext db,
    IPiiRedactor redactor) : IInsightDetector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<InsightSignal>> DetectAsync(Guid userId, DateTime referenceDate, CancellationToken ct)
    {
        var (curStart, curEnd) = MonthBounds(referenceDate.Year, referenceDate.Month);
        var bucket = curStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        var candidates = new List<Candidate>();
        candidates.AddRange(await DetectSpendingChangeAsync(userId, curStart, curEnd, bucket, ct));
        candidates.AddRange(await DetectBudgetRiskAsync(userId, referenceDate, curStart, curEnd, bucket, ct));
        candidates.AddRange(await DetectTrendAsync(userId, curStart, curEnd, bucket, ct));
        candidates.AddRange(await DetectSubscriptionCreepAsync(userId, curStart, curEnd, bucket, ct));
        candidates.AddRange(await DetectNewMerchantAsync(userId, curStart, curEnd, bucket, ct));

        return candidates
            .OrderByDescending(c => (int)c.Signal.Severity)
            .ThenByDescending(c => c.Magnitude)
            .Take(InsightThresholds.MaxSignalsPerRun)
            .Select(c => c.Signal)
            .ToList();
    }

    // --- SpendingChange: current month vs mean of the 3 prior full months, per category ----------

    private async Task<IEnumerable<Candidate>> DetectSpendingChangeAsync(
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

        var results = new List<Candidate>();
        foreach (var row in current)
        {
            var key = CategoryKey(row.CategoryId);
            var baseline = priorSum.GetValueOrDefault(key) / 3m;
            var delta = row.Amount - baseline;

            if (baseline < InsightThresholds.MinBaseline) continue;
            if (delta < InsightThresholds.SpendingChangeMinAbsolute) continue;

            var changePct = delta / baseline;
            if (changePct < InsightThresholds.SpendingChangeMinPct) continue;

            var severity = changePct >= InsightThresholds.SpendingChangeWarningPct
                ? AiInsightSeverity.Warning
                : AiInsightSeverity.Info;
            var subject = row.CategoryName ?? "Sem categoria";
            var dedupKey = $"SpendingChange:{key}:{bucket}";
            var pctText = FormatPct(changePct);

            var dataJson = JsonSerializer.Serialize(new
            {
                kind = "SpendingChange",
                categoryId = row.CategoryId,
                category = subject,
                currentAmount = row.Amount,
                baselineAmount = baseline,
                changePct,
                period = bucket,
            }, JsonOptions);

            results.Add(new Candidate(new InsightSignal(
                dedupKey,
                AiInsightKind.SpendingChange,
                severity,
                subject,
                curStart,
                curEnd,
                dataJson,
                dedupKey,
                $"{subject} subiu {pctText}% este mês",
                $"Você gastou {FormatMoney(row.Amount)} em {subject} este mês, {pctText}% acima da média de {FormatMoney(baseline)} dos últimos 3 meses."),
                changePct));
        }

        return results;
    }

    // --- BudgetRisk: PercentOfCap tiers (80/90 Warning, 100 Critical) ----------------------------

    private async Task<IEnumerable<Candidate>> DetectBudgetRiskAsync(
        Guid userId, DateTime referenceDate, DateTime curStart, DateTime curEnd, string bucket, CancellationToken ct)
    {
        var result = await budgets.GetBudgetsForMonthAsync(userId, referenceDate.Year, referenceDate.Month, ct);
        var results = new List<Candidate>();

        foreach (var row in result.Items)
        {
            var pct = row.PercentOfCap;
            int tier;
            AiInsightSeverity severity;
            if (pct >= 100m) { tier = 100; severity = AiInsightSeverity.Critical; }
            else if (pct >= 90m) { tier = 90; severity = AiInsightSeverity.Warning; }
            else if (pct >= InsightThresholds.BudgetRiskWarnTier) { tier = 80; severity = AiInsightSeverity.Warning; }
            else continue;

            var subject = row.CategoryName ?? "Geral";
            var subjectKey = row.CategoryId?.ToString() ?? "global";
            var dedupKey = $"BudgetRisk:{subjectKey}:{bucket}:{tier}";

            var dataJson = JsonSerializer.Serialize(new
            {
                kind = "BudgetRisk",
                categoryId = row.CategoryId,
                category = subject,
                percentOfCap = pct,
                limitAmount = row.LimitAmount,
                spentAmount = row.SpentAmount,
                tier,
                period = bucket,
            }, JsonOptions);

            results.Add(new Candidate(new InsightSignal(
                dedupKey,
                AiInsightKind.BudgetRisk,
                severity,
                subject,
                curStart,
                curEnd,
                dataJson,
                dedupKey,
                $"Orçamento de {subject} em {FormatPct(pct / 100m)}%",
                $"{subject} já consumiu {FormatPct(pct / 100m)}% do limite de {FormatMoney(row.LimitAmount)} ({FormatMoney(row.SpentAmount)} gastos)."),
                pct / 100m));
        }

        return results;
    }

    // --- Trend: monotone >=3-point net-worth move of >=10% ---------------------------------------

    private async Task<IEnumerable<Candidate>> DetectTrendAsync(
        Guid userId, DateTime curStart, DateTime curEnd, string bucket, CancellationToken ct)
    {
        var evo = await netWorth.GetEvolutionAsync(userId, 90, ct);
        var points = evo.Points.OrderBy(p => p.Date).ToList();
        if (points.Count < InsightThresholds.TrendMinPoints) return [];

        var up = true;
        var down = true;
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].NetWorth < points[i - 1].NetWorth) up = false;
            if (points[i].NetWorth > points[i - 1].NetWorth) down = false;
        }

        var start = points[0].NetWorth;
        var end = points[^1].NetWorth;
        if (start == 0m) return [];
        var isUp = up && end > start;
        var isDown = down && end < start;
        if (!isUp && !isDown) return [];

        var changePct = (end - start) / Math.Abs(start);
        if (Math.Abs(changePct) < InsightThresholds.TrendMinPct) return [];

        var severity = isDown ? AiInsightSeverity.Warning : AiInsightSeverity.Info;
        var dedupKey = $"Trend:netWorth:{bucket}";
        var direction = isDown ? "caiu" : "subiu";
        var pctText = FormatPct(Math.Abs(changePct));

        var dataJson = JsonSerializer.Serialize(new
        {
            kind = "Trend",
            metric = "netWorth",
            startValue = start,
            endValue = end,
            changePct,
            points = points.Count,
            period = bucket,
        }, JsonOptions);

        return
        [
            new Candidate(new InsightSignal(
                dedupKey,
                AiInsightKind.Trend,
                severity,
                "netWorth",
                curStart,
                curEnd,
                dataJson,
                dedupKey,
                $"Seu patrimônio {direction} {pctText}%",
                $"Seu patrimônio {direction} de forma consistente {pctText}% nos últimos meses, de {FormatMoney(start)} para {FormatMoney(end)}."),
                Math.Abs(changePct)),
        ];
    }

    // --- SubscriptionCreep: subscription recurring whose average rose >=15% -----------------------

    private async Task<IEnumerable<Candidate>> DetectSubscriptionCreepAsync(
        Guid userId, DateTime curStart, DateTime curEnd, string bucket, CancellationToken ct)
    {
        var recs = await db.RecurringTransactions
            .Where(r => r.UserId == userId
                && r.IsActive
                && r.IsSubscription
                && r.AmountIncreaseDetected
                && r.PreviousAverageAmount != null
                && r.PreviousAverageAmount > 0m)
            .ToListAsync(ct);

        var results = new List<Candidate>();
        foreach (var r in recs)
        {
            var prev = r.PreviousAverageAmount!.Value;
            var increasePct = (r.AverageAmount - prev) / prev;
            if (increasePct < InsightThresholds.SubscriptionCreepMinPct) continue;

            var severity = increasePct >= InsightThresholds.SubscriptionCreepCriticalPct
                ? AiInsightSeverity.Critical
                : AiInsightSeverity.Warning;
            var merchant = redactor.Redact(r.DisplayName);
            var newAvgText = r.AverageAmount.ToString("0.00", CultureInfo.InvariantCulture);
            var dedupKey = $"SubscriptionCreep:{r.Id}:{newAvgText}";
            var pctText = FormatPct(increasePct);

            var dataJson = JsonSerializer.Serialize(new
            {
                kind = "SubscriptionCreep",
                recurringId = r.Id,
                merchant,
                previousAverage = prev,
                newAverage = r.AverageAmount,
                increasePct,
                period = bucket,
            }, JsonOptions);

            results.Add(new Candidate(new InsightSignal(
                dedupKey,
                AiInsightKind.SubscriptionCreep,
                severity,
                merchant,
                curStart,
                curEnd,
                dataJson,
                dedupKey,
                $"{merchant} aumentou {pctText}%",
                $"A assinatura {merchant} subiu de {FormatMoney(prev)} para {FormatMoney(r.AverageAmount)} ({pctText}% a mais)."),
                increasePct));
        }

        return results;
    }

    // --- new-merchant fold: first-seen expense merchant this month >=R$100 -> SpendingChange ------

    private async Task<IEnumerable<Candidate>> DetectNewMerchantAsync(
        Guid userId, DateTime curStart, DateTime curEnd, string bucket, CancellationToken ct)
    {
        var windowStart = curStart.AddMonths(-6);

        var thisMonth = await db.Transactions
            .Where(t => t.UserId == userId
                && t.Type == TransactionType.Debit
                && !t.IsTransfer
                && !t.IsHidden
                && t.Status == TransactionStatus.Posted
                && t.Date >= curStart && t.Date <= curEnd)
            .Select(t => new { t.MerchantName, t.Description, t.Amount })
            .ToListAsync(ct);

        if (thisMonth.Count == 0) return [];

        var priorMerchants = (await db.Transactions
            .Where(t => t.UserId == userId
                && t.Type == TransactionType.Debit
                && !t.IsTransfer
                && !t.IsHidden
                && t.Status == TransactionStatus.Posted
                && t.Date >= windowStart && t.Date < curStart)
            .Select(t => new { t.MerchantName, t.Description })
            .ToListAsync(ct))
            .Select(t => MerchantNormalizer.Normalize(t.MerchantName ?? t.Description))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet();

        var results = new List<Candidate>();
        var groups = thisMonth
            .Select(t => new { Norm = MerchantNormalizer.Normalize(t.MerchantName ?? t.Description), Display = t.MerchantName ?? t.Description, t.Amount })
            .Where(t => !string.IsNullOrEmpty(t.Norm))
            .GroupBy(t => t.Norm);

        foreach (var g in groups)
        {
            if (priorMerchants.Contains(g.Key)) continue;

            var total = g.Sum(x => Math.Abs(x.Amount));
            if (total < InsightThresholds.NewMerchantMinAmount) continue;

            var merchant = redactor.Redact(g.First().Display);
            var hash = ShortHash(g.Key);
            var dedupKey = $"SpendingChange:merchant:{hash}:{bucket}";

            var dataJson = JsonSerializer.Serialize(new
            {
                kind = "SpendingChange",
                subkind = "newMerchant",
                merchant,
                currentAmount = total,
                period = bucket,
            }, JsonOptions);

            results.Add(new Candidate(new InsightSignal(
                dedupKey,
                AiInsightKind.SpendingChange,
                AiInsightSeverity.Info,
                merchant,
                curStart,
                curEnd,
                dataJson,
                dedupKey,
                $"Novo gasto em {merchant}",
                $"Apareceu um novo estabelecimento este mês: {merchant}, com {FormatMoney(total)} em gastos."),
                total / 1000m));
        }

        return results;
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static string CategoryKey(Guid? categoryId) => categoryId?.ToString() ?? "uncategorized";

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

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();

    private sealed record Candidate(InsightSignal Signal, decimal Magnitude);
}
