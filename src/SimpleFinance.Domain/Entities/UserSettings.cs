using SimpleFinance.Domain.Common;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// Per-user preferences row for CFG-02. One row per user (unique-indexed on UserId).
/// </summary>
public class UserSettings : UserOwnedEntity
{
    /// <summary>
    /// Validated and persisted, but not yet threaded into existing month-boundary queries
    /// (BudgetsEndpoints.cs's own GET handler already carries a TODO comment flagging this
    /// exact follow-up). Full timezone plumbing across every aggregate is a separate, larger
    /// effort outside this phase's stated success criteria.
    /// </summary>
    public string Timezone { get; set; } = "America/Sao_Paulo";

    /// <summary>
    /// Settable but display-only — no conversion logic exists anywhere, V2-06 multi-currency
    /// is out of v1 scope.
    /// </summary>
    public string BaseCurrency { get; set; } = "BRL";

    /// <summary>Mirrors Sync:DailySyncCronExpression's existing default.</summary>
    public string SyncCronExpression { get; set; } = "0 6,18 * * *";

    public string DefaultTheme { get; set; } = "light";

    // --- AI settings (15-01, AI-FND-07) ---
    // API keys may now be configured in the UI and stored here ENCRYPTED-AT-REST (ciphertext only,
    // never plaintext); the environment values (Ai:DeepSeekApiKey / Ai:OpenAiApiKey) remain a
    // fallback when the column is null. The settings endpoint still reports only whether each key is
    // present (DB column non-null OR env non-empty) and NEVER the key value (write-only).

    /// <summary>
    /// DeepSeek bearer key, AES-encrypted at rest (reuses <c>ITotpSecretProtector</c>). Null ⇒ fall
    /// back to the <c>Ai:DeepSeekApiKey</c> env value. Holds CIPHERTEXT ONLY — never plaintext, never
    /// returned by any endpoint.
    /// </summary>
    public string? DeepSeekApiKeyEncrypted { get; set; }

    /// <summary>
    /// OpenAI bearer key, AES-encrypted at rest (reuses <c>ITotpSecretProtector</c>). Null ⇒ fall back
    /// to the <c>Ai:OpenAiApiKey</c> env value. Holds CIPHERTEXT ONLY — never plaintext, never returned
    /// by any endpoint.
    /// </summary>
    public string? OpenAiApiKeyEncrypted { get; set; }

    /// <summary>Master AI switch. When false, no cloud AI feature runs regardless of the per-feature flags.</summary>
    public bool AiEnabled { get; set; } = false;

    /// <summary>Per-feature toggle: LLM-assisted transaction categorization.</summary>
    public bool AiCategorizationEnabled { get; set; } = false;

    /// <summary>Per-feature toggle: the AI chat assistant.</summary>
    public bool AiAssistantEnabled { get; set; } = false;

    /// <summary>Per-feature toggle: AI-generated insights.</summary>
    public bool AiInsightsEnabled { get; set; } = false;

    /// <summary>Per-feature toggle: AI anomaly detection.</summary>
    public bool AiAnomalyEnabled { get; set; } = false;

    /// <summary>Per-feature toggle: AI spending forecast.</summary>
    public bool AiForecastEnabled { get; set; } = false;

    /// <summary>Per-feature toggle: AI monthly summary.</summary>
    public bool AiMonthlySummaryEnabled { get; set; } = false;

    /// <summary>Confidence threshold (0..1) above which an AI categorization is auto-applied.</summary>
    public double AiAutoApplyThreshold { get; set; } = 0.85;

    /// <summary>Hard monthly USD cap for AI cloud spend. 0 ⇒ zero cloud calls (app stays fully functional).</summary>
    public decimal AiMonthlyBudgetUsd { get; set; } = 3.00m;

    /// <summary>
    /// Model for chat/assistant/categorization. Config-driven. Kept as "deepseek-chat" because DeepSeek
    /// already routes this name to V4-flash in NON-thinking mode (verified: response model = deepseek-v4-flash),
    /// which is what tool-calling needs — the explicit "deepseek-v4-flash" id defaults to thinking mode
    /// (populates reasoning_content, risks 400 on tool-call turns, and can leave content empty).
    /// </summary>
    public string AiChatModel { get; set; } = "deepseek-chat";

    /// <summary>Model reserved for monthly summaries (thinking/reasoning). "deepseek-reasoner" = V4-flash thinking mode.</summary>
    public string AiSummaryModel { get; set; } = "deepseek-reasoner";

    /// <summary>Language for AI-generated text; follows the app i18n default.</summary>
    public string AiLanguage { get; set; } = "pt-BR";
}
