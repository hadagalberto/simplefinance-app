using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Ai;

/// <summary>
/// Per-request <c>Authorization: Bearer</c> injector shared by the OpenAI-compatible HTTP clients.
/// Replaces the old static <c>DefaultRequestHeaders.Authorization</c> set at DI-registration time: the
/// key is resolved on EVERY outbound request via <see cref="IAiKeyProvider"/>, so a key edited in
/// Settings → IA (or a fixed whitespace-polluted value) takes effect on the next call without a restart.
/// Message handlers are singletons and run in both HTTP-request and Hangfire-job contexts, so the scoped
/// <see cref="IAiKeyProvider"/> is resolved through a fresh scope from <see cref="IServiceScopeFactory"/>.
/// When no key is configured (DB null + env empty), no header is set — the downstream call 401s exactly
/// as the env-only path did.
/// </summary>
public abstract class AiBearerHandler(IServiceScopeFactory scopeFactory, AiProvider provider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var keyProvider = scope.ServiceProvider.GetRequiredService<IAiKeyProvider>();
        var key = await keyProvider.GetKeyAsync(provider, cancellationToken);
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Per-request DeepSeek bearer injector (see <see cref="AiBearerHandler"/>).</summary>
public sealed class DeepSeekBearerHandler(IServiceScopeFactory scopeFactory)
    : AiBearerHandler(scopeFactory, AiProvider.DeepSeek);

/// <summary>
/// Per-request OpenAI bearer injector — the OpenAI twin of <see cref="DeepSeekBearerHandler"/> (same
/// scope-per-request resolution + trim + env fallback contract).
/// </summary>
public sealed class OpenAiBearerHandler(IServiceScopeFactory scopeFactory)
    : AiBearerHandler(scopeFactory, AiProvider.OpenAi);

/// <summary>Per-request OpenRouter bearer injector (see <see cref="AiBearerHandler"/>).</summary>
public sealed class OpenRouterBearerHandler(IServiceScopeFactory scopeFactory)
    : AiBearerHandler(scopeFactory, AiProvider.OpenRouter);
