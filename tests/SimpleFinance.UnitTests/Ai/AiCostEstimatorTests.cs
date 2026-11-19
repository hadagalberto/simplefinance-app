using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleFinance.Infrastructure.Ai;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// AI-FND-04 (15-03 Task 3): the cost estimator turns a model name + token counts into an
/// EstimatedCostUsd from the config-driven rate table. Rates come from config (never hardcoded model
/// strings) so the deepseek rename is a config edit. Unknown models estimate 0 and never throw.
/// </summary>
public class AiCostEstimatorTests
{
    private static AiCostEstimator Create()
    {
        var options = new AiOptions
        {
            Pricing = new(StringComparer.OrdinalIgnoreCase)
            {
                ["deepseek-chat"] = new() { InputPerMillion = 0.14m, CachedInputPerMillion = 0.0028m, OutputPerMillion = 0.28m },
                ["text-embedding-3-small"] = new() { InputPerMillion = 0.02m },
            },
        };
        return new AiCostEstimator(Options.Create(options), NullLogger<AiCostEstimator>.Instance);
    }

    [Fact]
    public void Estimate_KnownChatModel_UsesInputAndOutputRates()
    {
        var cost = Create().Estimate("deepseek-chat", promptTokens: 1000, completionTokens: 500);

        // (1000/1e6)*0.14 + (500/1e6)*0.28 = 0.00014 + 0.00014 = 0.00028
        Assert.Equal(0.00028m, cost, precision: 8);
    }

    [Fact]
    public void Estimate_EmbeddingModel_UsesInputRateOnly()
    {
        var cost = Create().Estimate("text-embedding-3-small", promptTokens: 1000, completionTokens: 0);

        // (1000/1e6)*0.02 = 0.00002; no output rate applies
        Assert.Equal(0.00002m, cost, precision: 8);
    }

    [Fact]
    public void Estimate_UnknownModel_ReturnsZero_WithoutThrowing()
    {
        var cost = Create().Estimate("some-unlisted-model", promptTokens: 5000, completionTokens: 5000);

        Assert.Equal(0m, cost);
    }
}
