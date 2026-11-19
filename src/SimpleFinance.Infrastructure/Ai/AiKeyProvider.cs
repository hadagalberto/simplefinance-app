using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Auth;
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
        ResolveAsync(deepSeek: true, ct);

    public Task<string?> GetOpenAiKeyAsync(CancellationToken ct) =>
        ResolveAsync(deepSeek: false, ct);

    private async Task<string?> ResolveAsync(bool deepSeek, CancellationToken ct)
    {
        var settings = await db.UserSettings
            .OrderBy(s => s.CreatedAt)
            .Select(s => new { s.DeepSeekApiKeyEncrypted, s.OpenAiApiKeyEncrypted })
            .FirstOrDefaultAsync(ct);

        var encrypted = deepSeek ? settings?.DeepSeekApiKeyEncrypted : settings?.OpenAiApiKeyEncrypted;
        if (!string.IsNullOrWhiteSpace(encrypted))
        {
            var decrypted = protector.Decrypt(encrypted).Trim();
            if (decrypted.Length > 0)
            {
                return decrypted;
            }
        }

        var envValue = (deepSeek ? _options.DeepSeekApiKey : _options.OpenAiApiKey)?.Trim();
        return string.IsNullOrEmpty(envValue) ? null : envValue;
    }
}
