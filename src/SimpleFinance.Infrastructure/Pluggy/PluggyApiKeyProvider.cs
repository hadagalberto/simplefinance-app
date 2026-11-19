using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SimpleFinance.Infrastructure.Pluggy.Dtos;

namespace SimpleFinance.Infrastructure.Pluggy;

/// <summary>
/// PLG-01: obtains and caches the Pluggy API Key (~2h TTL), transparently refreshing before
/// expiry (with a safety margin) or on-demand after a 401 (see PluggyClient). Registered as a
/// singleton (PluggyServiceCollectionExtensions) — the cache must be shared app-wide, never
/// per-request. Resolves its HttpClient via IHttpClientFactory rather than typed-client
/// constructor injection, since typed-client HttpClient lifetime management assumes a
/// transient/scoped consumer, not a singleton.
/// </summary>
public sealed class PluggyApiKeyProvider
{
    public const string HttpClientName = "PluggyAuth";

    // Refresh at ~90 minutes of the 2h TTL, per 02-RESEARCH.md's Code Examples section and
    // PITFALLS.md #4 (refresh-with-margin, never cache-and-forget).
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ApiKeyTtl = TimeSpan.FromHours(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PluggyOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _apiKey;
    private DateTimeOffset _issuedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public PluggyApiKeyProvider(IHttpClientFactory httpClientFactory, IOptions<PluggyOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    /// <summary>Returns the cached API key, refreshing first if within <see cref="Margin"/> of expiry.</summary>
    public Task<string> GetApiKeyAsync(CancellationToken ct)
    {
        if (_apiKey is not null && DateTimeOffset.UtcNow < _expiresAt - Margin)
        {
            return Task.FromResult(_apiKey);
        }

        return RefreshAsync(forceIgnoreMargin: false, ct);
    }

    /// <summary>
    /// Bypasses the margin check and forces a fresh POST /auth — invoked by PluggyClient after
    /// any 401 (PITFALLS.md #4). Still SemaphoreSlim-guarded: concurrent callers awaiting a
    /// refresh already in progress receive the same refreshed key rather than issuing a second
    /// duplicate POST /auth.
    /// </summary>
    public Task<string> ForceRefreshAsync(CancellationToken ct) => RefreshAsync(forceIgnoreMargin: true, ct);

    private async Task<string> RefreshAsync(bool forceIgnoreMargin, CancellationToken ct)
    {
        // Captured BEFORE waiting on the lock: if another caller already completed a refresh
        // while we were waiting (issued a newer key than what we saw at entry), we reuse it
        // instead of a duplicate POST /auth — this is what makes concurrent GetApiKeyAsync AND
        // concurrent ForceRefreshAsync callers dedup onto a single in-flight network call.
        var snapshotIssuedAt = _issuedAt;

        await _lock.WaitAsync(ct);
        try
        {
            if (_apiKey is not null && _issuedAt > snapshotIssuedAt)
            {
                return _apiKey;
            }

            if (!forceIgnoreMargin && _apiKey is not null && DateTimeOffset.UtcNow < _expiresAt - Margin)
            {
                return _apiKey;
            }

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await client.PostAsJsonAsync(
                "/auth",
                new PluggyAuthRequest(_options.ClientId, _options.ClientSecret),
                ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<PluggyAuthResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("Pluggy POST /auth returned an empty response body.");

            _apiKey = body.ApiKey;
            _issuedAt = DateTimeOffset.UtcNow;
            _expiresAt = _issuedAt.Add(ApiKeyTtl);
            return _apiKey;
        }
        finally
        {
            _lock.Release();
        }
    }
}
