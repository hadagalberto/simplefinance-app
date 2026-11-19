using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.Usage;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// AI-FND-04 (15-03 Task 3): the usage recorder writes exactly one AiUsageLog row per cloud call,
/// scoped to the caller's UserId, carrying the passed feature/provider/model/tokens and the
/// estimator's computed cost. Verified against EF Core's InMemory provider (mirrors GoalServiceTests).
/// </summary>
public class AiUsageRecorderTests
{
    private static SimpleFinanceDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static AiCostEstimator CreateEstimator()
    {
        var options = new AiOptions
        {
            Pricing = new(StringComparer.OrdinalIgnoreCase)
            {
                ["deepseek-chat"] = new() { InputPerMillion = 0.14m, CachedInputPerMillion = 0.0028m, OutputPerMillion = 0.28m },
            },
        };
        return new AiCostEstimator(Options.Create(options), NullLogger<AiCostEstimator>.Instance);
    }

    [Fact]
    public async Task RecordAsync_WritesExactlyOneRow_WithPassedValuesAndComputedCost()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var recorder = new AiUsageRecorder(db, CreateEstimator());

        await recorder.RecordAsync(
            userId,
            AiFeature.Categorization,
            AiProvider.DeepSeek,
            model: "deepseek-chat",
            promptTokens: 1000,
            completionTokens: 500,
            CancellationToken.None);

        var rows = await db.AiUsageLogs.ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(userId, row.UserId);
        Assert.Equal(AiFeature.Categorization, row.Feature);
        Assert.Equal(AiProvider.DeepSeek, row.Provider);
        Assert.Equal("deepseek-chat", row.Model);
        Assert.Equal(1000, row.PromptTokens);
        Assert.Equal(500, row.CompletionTokens);
        Assert.Equal(0.00028m, row.EstimatedCostUsd, precision: 8);
        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.NotEqual(default, row.CreatedAt);
    }

    [Fact]
    public async Task RecordAsync_UnknownModel_StillWritesRow_WithZeroCost()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var recorder = new AiUsageRecorder(db, CreateEstimator());

        await recorder.RecordAsync(
            userId,
            AiFeature.Embedding,
            AiProvider.OpenAi,
            model: "mystery-model",
            promptTokens: 800,
            completionTokens: 0,
            CancellationToken.None);

        var row = Assert.Single(await db.AiUsageLogs.ToListAsync());
        Assert.Equal(0m, row.EstimatedCostUsd);
        Assert.Equal("mystery-model", row.Model);
    }
}
