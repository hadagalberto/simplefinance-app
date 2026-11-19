using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Auth;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai;

/// <summary>
/// AI-FND-07/08 response shape for GET/PUT /api/ai/settings. Carries every editable AI field from
/// <see cref="UserSettings"/> plus a presence boolean per provider key
/// (<see cref="DeepSeekKeyPresent"/> / <see cref="OpenAiKeyPresent"/> / <see cref="AnthropicKeyPresent"/> /
/// <see cref="OpenRouterKeyPresent"/>). The key VALUE is NEVER part
/// of this record — the settings surface reports only whether each env key is configured (T-15-14).
/// </summary>
public sealed record AiSettingsResult(
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
    bool DeepSeekKeyPresent,
    bool OpenAiKeyPresent,
    bool AnthropicKeyPresent,
    bool OpenRouterKeyPresent);

/// <summary>
/// AI-FND-07/08: get-or-create read + update of the AI slice of <see cref="UserSettings"/>, and the
/// single place that computes each provider's key-present boolean from <see cref="AiOptions"/> (env
/// only). Deliberately in Infrastructure (not Application) — it depends directly on
/// <see cref="SimpleFinanceDbContext"/>, mirroring <c>PreferencesService</c>'s documented rationale
/// (Application cannot reference Infrastructure without a circular project reference). AiEndpoints.cs
/// is a thin HTTP wrapper over this class only — no business logic is duplicated there.
///
/// The API key VALUE is never read into the response: only <c>!string.IsNullOrWhiteSpace(key)</c> is
/// projected, so a settings response can never leak a secret (T-15-14).
/// </summary>
public sealed class AiSettingsService(
    SimpleFinanceDbContext db,
    IOptions<AiOptions> options,
    ITotpSecretProtector keyProtector)
{
    private readonly AiOptions _options = options.Value;

    /// <summary>
    /// Get-or-create read path (PreferencesService.GetOrCreateAsync shape): materializes a
    /// UserSettings row with the entity's C# defaults on first access for a user, then returns the
    /// AI settings projection. Idempotent.
    /// </summary>
    public async Task<AiSettingsResult> GetAsync(Guid userId, CancellationToken ct)
    {
        var settings = await GetOrCreateAsync(userId, ct);
        return ToResult(settings);
    }

    /// <summary>
    /// Validated-command update path. The caller (AiEndpoints) runs <see cref="AiSettingsValidator"/>
    /// before invoking this, so the command is already known-good (threshold 0-1, budget ≥ 0, models
    /// non-empty). Maps the command onto the row, bumps UpdatedAt, saves, and returns the fresh
    /// projection.
    /// </summary>
    public async Task<AiSettingsResult> UpdateAsync(Guid userId, UpdateAiSettingsCommand command, CancellationToken ct)
    {
        var settings = await GetOrCreateAsync(userId, ct);

        settings.AiEnabled = command.AiEnabled;
        settings.AiCategorizationEnabled = command.AiCategorizationEnabled;
        settings.AiAssistantEnabled = command.AiAssistantEnabled;
        settings.AiInsightsEnabled = command.AiInsightsEnabled;
        settings.AiAnomalyEnabled = command.AiAnomalyEnabled;
        settings.AiForecastEnabled = command.AiForecastEnabled;
        settings.AiMonthlySummaryEnabled = command.AiMonthlySummaryEnabled;
        settings.AiAutoApplyThreshold = command.AiAutoApplyThreshold;
        settings.AiMonthlyBudgetUsd = command.AiMonthlyBudgetUsd;
        settings.AiChatModel = command.AiChatModel;
        settings.AiSummaryModel = command.AiSummaryModel;
        settings.AiLanguage = command.AiLanguage;

        // Write-only key tri-state: null ⇒ unchanged; "" ⇒ clear (revert to env); value ⇒ Trim + encrypt.
        // The at-rest AES protector (ITotpSecretProtector) is reused here — an acceptable single-user
        // reuse of the same distinct-from-JWT encryption key; the column ever only holds ciphertext.
        settings.DeepSeekApiKeyEncrypted = ApplyKeyUpdate(settings.DeepSeekApiKeyEncrypted, command.DeepSeekApiKey);
        settings.OpenAiApiKeyEncrypted = ApplyKeyUpdate(settings.OpenAiApiKeyEncrypted, command.OpenAiApiKey);
        settings.AnthropicApiKeyEncrypted = ApplyKeyUpdate(settings.AnthropicApiKeyEncrypted, command.AnthropicApiKey);
        settings.OpenRouterApiKeyEncrypted = ApplyKeyUpdate(settings.OpenRouterApiKeyEncrypted, command.OpenRouterApiKey);

        settings.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        return ToResult(settings);
    }

    /// <summary>
    /// Resolves the tri-state write-only key update to the new ciphertext column value: absent (null) keeps
    /// the current value; explicit empty string clears it (null column ⇒ env fallback); a non-empty value is
    /// trimmed and encrypted. Never stores plaintext.
    /// </summary>
    private string? ApplyKeyUpdate(string? currentEncrypted, string? submitted)
    {
        if (submitted is null)
        {
            return currentEncrypted; // field absent ⇒ leave unchanged
        }

        var trimmed = submitted.Trim();
        return trimmed.Length == 0
            ? null // explicit clear ⇒ revert to env fallback
            : keyProtector.Encrypt(trimmed);
    }

    private async Task<UserSettings> GetOrCreateAsync(Guid userId, CancellationToken ct)
    {
        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (settings is not null)
        {
            return settings;
        }

        var now = DateTime.UtcNow;
        settings = new UserSettings
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.UserSettings.Add(settings);
        await db.SaveChangesAsync(ct);

        return settings;
    }

    private AiSettingsResult ToResult(UserSettings s) =>
        new(
            s.AiEnabled,
            s.AiCategorizationEnabled,
            s.AiAssistantEnabled,
            s.AiInsightsEnabled,
            s.AiAnomalyEnabled,
            s.AiForecastEnabled,
            s.AiMonthlySummaryEnabled,
            s.AiAutoApplyThreshold,
            s.AiMonthlyBudgetUsd,
            s.AiChatModel,
            s.AiSummaryModel,
            s.AiLanguage,
            // Presence only — the key value itself is NEVER read into the response (T-15-14). A key is
            // "present" when the encrypted DB column is set OR the env fallback is non-empty.
            DeepSeekKeyPresent: s.DeepSeekApiKeyEncrypted is not null || !string.IsNullOrWhiteSpace(_options.DeepSeekApiKey),
            OpenAiKeyPresent: s.OpenAiApiKeyEncrypted is not null || !string.IsNullOrWhiteSpace(_options.OpenAiApiKey),
            AnthropicKeyPresent: s.AnthropicApiKeyEncrypted is not null || !string.IsNullOrWhiteSpace(_options.AnthropicApiKey),
            OpenRouterKeyPresent: s.OpenRouterApiKeyEncrypted is not null || !string.IsNullOrWhiteSpace(_options.OpenRouterApiKey));
}
