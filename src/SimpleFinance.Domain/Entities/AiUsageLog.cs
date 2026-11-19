using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// One row per cloud AI call (LLM chat or embeddings), per AI-FND-04. Records the feature that
/// triggered the call, the provider/model, token counts, and the estimated USD cost. The monthly
/// SUM of <see cref="EstimatedCostUsd"/> per user feeds the 15-04 budget kill-switch (T-15-06), so
/// every recorded call contributes to the hard cap. Per-user (inherits <see cref="UserOwnedEntity"/>
/// for UserId/Id/CreatedAt/UpdatedAt/DeletedAt).
/// </summary>
public class AiUsageLog : UserOwnedEntity
{
    public AiFeature Feature { get; set; }

    public AiProvider Provider { get; set; }

    public string Model { get; set; } = string.Empty;

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public decimal EstimatedCostUsd { get; set; }
}
