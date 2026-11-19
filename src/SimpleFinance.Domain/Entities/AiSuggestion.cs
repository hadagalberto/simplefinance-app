using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A reviewable AI proposal, per AI-CAT-03/05/06. Created by <c>AiCategorizationJob</c> (16-04) when
/// the cascade's decision is confident enough to surface but below the user's
/// <c>UserSettings.AiAutoApplyThreshold</c> — so it is NOT auto-applied. The suggestion records the
/// proposed change (<see cref="SuggestedPayload"/>, a small jsonb blob e.g.
/// <c>{"categoryId":"&lt;guid&gt;"}</c>), the cascade <see cref="Confidence"/>/<see cref="Rationale"/>,
/// and its provenance (<see cref="Source"/>). Its <see cref="Status"/> starts <c>Pending</c>; 16-05's
/// accept/reject endpoints transition it and stamp <see cref="DecidedAt"/>. Accepting a Category
/// suggestion applies the category as <c>CategorySource.AiSuggested</c> (a trusted, never-re-touched
/// label + a positive kNN neighbour). Per-user (inherits <see cref="UserOwnedEntity"/> for
/// UserId/Id/CreatedAt/UpdatedAt/DeletedAt).
/// </summary>
public class AiSuggestion : UserOwnedEntity
{
    /// <summary>What kind of change is proposed. Only <see cref="AiSuggestionType.Category"/> this phase.</summary>
    public AiSuggestionType Type { get; set; }

    /// <summary>The transaction this suggestion targets (for Category suggestions). Nullable so future
    /// non-transaction suggestion types (Rule/Budget/Goal) can omit it.</summary>
    public Guid? TransactionId { get; set; }

    /// <summary>The proposed change as a compact jsonb document, e.g. <c>{"categoryId":"&lt;guid&gt;"}</c>.</summary>
    public string SuggestedPayload { get; set; } = string.Empty;

    /// <summary>Cascade confidence 0..1 for the proposed change (below the auto-apply threshold).</summary>
    public double Confidence { get; set; }

    /// <summary>Short human-readable justification — populated when the LLM level produced the decision.</summary>
    public string? Rationale { get; set; }

    /// <summary>Which cascade level produced the suggestion (Knn ⇒ Embedding, Llm ⇒ Llm).</summary>
    public AiSuggestionSource Source { get; set; }

    /// <summary>Lifecycle state. Starts <see cref="AiSuggestionStatus.Pending"/>.</summary>
    public AiSuggestionStatus Status { get; set; }

    /// <summary>When the user accepted/rejected this suggestion (16-05). Null while Pending.</summary>
    public DateTime? DecidedAt { get; set; }
}
