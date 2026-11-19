namespace SimpleFinance.Application.Ai;

/// <summary>
/// AI-FND-08: the editable AI settings a user submits via PUT /api/ai/settings. The provider keys are
/// WRITE-ONLY (<see cref="DeepSeekApiKey"/> / <see cref="OpenAiApiKey"/> /
/// <see cref="AnthropicApiKey"/> / <see cref="OpenRouterApiKey"/>): they are accepted here, stored
/// encrypted at rest, and never returned by any endpoint. Their tri-state semantics are:
/// <list type="bullet">
///   <item><c>null</c> (field absent from the JSON) ⇒ leave the stored key unchanged;</item>
///   <item>a non-empty string ⇒ Trim + encrypt + store (overrides the env fallback);</item>
///   <item>an explicit empty string <c>""</c> ⇒ clear the stored key (revert to the env fallback).</item>
/// </list>
/// Validated by <see cref="AiSettingsValidator"/> before it ever touches <c>UserSettings</c>.
/// </summary>
public sealed record UpdateAiSettingsCommand(
    bool AiEnabled,
    bool AiCategorizationEnabled,
    bool AiAssistantEnabled,
    bool AiInsightsEnabled,
    bool AiAnomalyEnabled,
    bool AiForecastEnabled,
    bool AiMonthlySummaryEnabled,
    double AiAutoApplyThreshold,
    decimal AiMonthlyBudgetUsd,
    string AiChatModel,
    string AiSummaryModel,
    string AiLanguage,
    string? DeepSeekApiKey = null,
    string? OpenAiApiKey = null,
    string? AnthropicApiKey = null,
    string? OpenRouterApiKey = null);
