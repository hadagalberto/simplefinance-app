using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Auth;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai;

/// <summary>
/// AI-FND-07: the runtime key resolver behind <see cref="IAiKeyProvider"/>. Single-user app — reads the
/// one <c>UserSettings</c> row (oldest-first, deterministic) and prefers its AES-decrypted key column,
/// falling back to the env-bound <see cref="AiOptions"/> value, trimming either. Reuses the existing
/// <see cref="ITotpSecretProtector"/> (AES-256-GCM at rest) rather than standing up a second at-rest
/// crypto stack — an acceptable single-user reuse of the same distinct-from-JWT encryption key.
/// </summary>
public sealed class AiKeyProvider(
    SimpleFinanceDbContext db,
    ITotpSecretProtector protector,
    IOptions<AiOptions> options) : IAiKeyProvider
{
    private readonly AiOptions _options = options.Value;

    public Task<string?> GetDeepSeekKeyAsync(CancellationToken ct) =>
        GetKeyAsync(AiProvider.DeepSeek, ct);

    public Task<string?> GetOpenAiKeyAsync(CancellationToken ct) =>
        GetKeyAsync(AiProvider.OpenAi, ct);

    public async Task<string?> GetKeyAsync(AiProvider provider, CancellationToken ct)
    {
        var settings = await db.UserSettings
            .OrderBy(s => s.CreatedAt)
            .Select(s => new
            {
                s.DeepSeekApiKeyEncrypted,
                s.OpenAiApiKeyEncrypted,
                s.AnthropicApiKeyEncrypted,
                s.OpenRouterApiKeyEncrypted,
            })
            .FirstOrDefaultAsync(ct);

        var encrypted = provider switch
        {
            AiProvider.DeepSeek => settings?.DeepSeekApiKeyEncrypted,
            AiProvider.OpenAi => settings?.OpenAiApiKeyEncrypted,
            AiProvider.Anthropic => settings?.AnthropicApiKeyEncrypted,
            AiProvider.OpenRouter => settings?.OpenRouterApiKeyEncrypted,
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(encrypted))
        {
            var decrypted = protector.Decrypt(encrypted).Trim();
            if (decrypted.Length > 0)
            {
                return decrypted;
            }
        }

        var envValue = (provider switch
        {
            AiProvider.DeepSeek => _options.DeepSeekApiKey,
            AiProvider.OpenAi => _options.OpenAiApiKey,
            AiProvider.Anthropic => _options.AnthropicApiKey,
            AiProvider.OpenRouter => _options.OpenRouterApiKey,
            _ => null,
        })?.Trim();

        return string.IsNullOrEmpty(envValue) ? null : envValue;
    }
}
