using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SimpleFinance.Infrastructure.Ai;

/// <summary>
/// AI-FND-04 cost estimation. Turns a model name + token counts into an EstimatedCostUsd using the
/// config-driven <see cref="AiOptions.Pricing"/> rate table:
/// <c>(promptTokens/1e6)*inputRate + (completionTokens/1e6)*outputRate</c>. Rates live in config
/// (never hardcoded model strings) so the deepseek rename / any price change is a config edit
/// (RESEARCH Pitfall 2). A conservative v1 uses the cache-miss input rate. Unknown models estimate
/// 0 (the row is still recorded) and log a warning — never throw, never block a call (a mistyped
/// model must not silently disable AI or double-charge). Registered as a Singleton (stateless).
/// </summary>
public sealed class AiCostEstimator(IOptions<AiOptions> options, ILogger<AiCostEstimator> logger)
{
    private readonly AiOptions _options = options.Value;

    public decimal Estimate(string model, int promptTokens, int completionTokens)
    {
        var pricing = ResolvePricing(model);
        if (pricing is null)
        {
            logger.LogWarning(
                "No AI cost rate configured for model '{Model}'; estimating $0.00. Add it under Ai:Pricing.",
                model);
            return 0m;
        }

        var inputCost = (promptTokens / 1_000_000m) * pricing.InputPerMillion;
        var outputCost = (completionTokens / 1_000_000m) * (pricing.OutputPerMillion ?? 0m);
        return inputCost + outputCost;
    }

    private ModelPricing? ResolvePricing(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        if (_options.Pricing.TryGetValue(model, out var pricing))
        {
            return pricing;
        }

        // The config-bound dictionary may not carry a case-insensitive comparer; fall back to a
        // case-insensitive scan so casing differences in config vs request don't zero the estimate.
        foreach (var kv in _options.Pricing)
        {
            if (string.Equals(kv.Key, model, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value;
            }
        }

        return null;
    }
}
