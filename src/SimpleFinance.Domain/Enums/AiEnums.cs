namespace SimpleFinance.Domain.Enums;

/// <summary>
/// Which AI capability a cloud call (and its usage/cost row) belongs to. Matches the
/// per-feature toggles on <see cref="Entities.UserSettings"/> and the Feature column on
/// AiUsageLog (15-03). Used for cost attribution and per-feature budget reporting.
/// </summary>
public enum AiFeature
{
    Categorization,
    Assistant,
    Insights,
    Anomaly,
    Forecast,
    MonthlySummary,
    Embedding
}

/// <summary>
/// Which external provider serviced a cloud call. DeepSeek backs chat/completions
/// (OpenAI-compatible API); OpenAi backs embeddings (text-embedding-3-small).
/// </summary>
public enum AiProvider
{
    DeepSeek,
    OpenAi
}

/// <summary>
/// What kind of change an <see cref="Entities.AiSuggestion"/> proposes. Only
/// <see cref="Category"/> is produced this phase (AI-CAT-03/05); Rule/Budget/Goal are reserved for
/// the Phase 17 assistant write-tools. Persisted as <c>int</c> — APPEND-ONLY, never reorder.
/// </summary>
public enum AiSuggestionType
{
    Category,
    Rule,
    Budget,
    Goal
}

/// <summary>
/// Which cascade level produced an <see cref="Entities.AiSuggestion"/>, mapped from the 16-03
/// <c>ClassificationSource</c> (Knn ⇒ <see cref="Embedding"/>, Llm ⇒ <see cref="Llm"/>).
/// <see cref="Statistical"/> is reserved for future non-AI heuristics. Persisted as <c>int</c> —
/// APPEND-ONLY, never reorder.
/// </summary>
public enum AiSuggestionSource
{
    Embedding,
    Llm,
    Statistical
}

/// <summary>
/// Lifecycle of an <see cref="Entities.AiSuggestion"/>. A below-threshold classification is created
/// <see cref="Pending"/>; the user (16-05) moves it to <see cref="Accepted"/>/<see cref="Rejected"/>;
/// <see cref="AutoApplied"/> records a suggestion that was instead auto-applied; <see cref="Expired"/>
/// is reserved for stale-suggestion cleanup. Persisted as <c>int</c> — APPEND-ONLY, never reorder.
/// </summary>
public enum AiSuggestionStatus
{
    Pending,
    Accepted,
    Rejected,
    AutoApplied,
    Expired
}
