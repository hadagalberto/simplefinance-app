using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.Usage;

/// <summary>
/// AI-FND-04: writes exactly one <see cref="AiUsageLog"/> row per cloud AI call. Computes the
/// estimated cost via <see cref="AiCostEstimator"/> from the config rate table, then persists the
/// token counts and cost scoped to the caller's UserId — the data the 15-04 budget kill-switch sums
/// per month (T-15-06). Scoped (touches <see cref="SimpleFinanceDbContext"/>); the provider clients
/// (15-05) call it after every request so accounting cannot be skipped.
/// </summary>
public sealed class AiUsageRecorder(SimpleFinanceDbContext db, AiCostEstimator estimator) : IAiUsageRecorder
{
    public async Task RecordAsync(
        Guid userId,
        AiFeature feature,
        AiProvider provider,
        string model,
        int promptTokens,
        int completionTokens,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var log = new AiUsageLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Feature = feature,
            Provider = provider,
            Model = model,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            EstimatedCostUsd = estimator.Estimate(model, promptTokens, completionTokens),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.AiUsageLogs.Add(log);
        await db.SaveChangesAsync(ct);
    }
}
