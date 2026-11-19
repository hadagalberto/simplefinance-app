namespace SimpleFinance.Application.Ai;

/// <summary>
/// AI-CAT-06: one row of the "Sugestões da IA" queue (GET /api/ai/suggestions). Flattens an
/// <c>AiSuggestion</c> together with its target transaction (description/merchant) and the resolved
/// suggested-category name so the frontend can render + accept/reject without a second round-trip.
/// <see cref="SuggestedCategoryId"/> is parsed out of the suggestion's jsonb payload
/// (<c>{"categoryId":"&lt;guid&gt;"}</c>); <see cref="Source"/>/<see cref="Status"/> are the enum names.
/// </summary>
public sealed record SuggestionResponse(
    Guid Id,
    Guid? TransactionId,
    string TransactionDescription,
    string? MerchantName,
    Guid? SuggestedCategoryId,
    string? SuggestedCategoryName,
    double Confidence,
    string? Rationale,
    string Source,
    string Status,
    DateTime CreatedAt);

/// <summary>
/// AI-CAT-06: body of POST /api/ai/suggestions/bulk-accept — the ids of the pending suggestions to
/// accept in one call. Validated by <see cref="BulkAcceptSuggestionsValidator"/> (non-empty, capped,
/// no empty Guids) before it reaches the service.
/// </summary>
public sealed record BulkAcceptSuggestionsCommand(IReadOnlyList<Guid> Ids);
