using SimpleFinance.Application.Ai;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.Anthropic;
using SimpleFinance.Infrastructure.Ai.DeepSeek;
using SimpleFinance.Infrastructure.Ai.OpenRouter;

namespace SimpleFinance.Infrastructure.Ai.Routing;

/// <summary>
/// The <see cref="ILlmClient"/> every caller receives. Picks the concrete provider client per call from
/// <see cref="LlmChatRequest.Model"/> via <see cref="LlmProviderResolver"/> and delegates verbatim; each
/// concrete client still enforces its own redact → budget-check → send → record invariant. Callers keep
/// passing the model name from UserSettings, so switching provider is a Settings → IA edit only.
/// Delegates are resolved lazily so an unused provider's client is never constructed.
/// </summary>
public sealed class RoutingLlmClient : ILlmClient
{
    private readonly Func<AiProvider, ILlmClient> _clientFor;

    public RoutingLlmClient(
        Lazy<DeepSeekLlmClient> deepSeek,
        Lazy<AnthropicLlmClient> anthropic,
        Lazy<OpenRouterLlmClient> openRouter)
        : this(provider => provider switch
        {
            AiProvider.Anthropic => anthropic.Value,
            AiProvider.OpenRouter => openRouter.Value,
            _ => deepSeek.Value,
        })
    {
    }

    /// <summary>Test seam: supply the provider → client mapping directly.</summary>
    public RoutingLlmClient(Func<AiProvider, ILlmClient> clientFor)
    {
        _clientFor = clientFor;
    }

    public Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct) =>
        _clientFor(LlmProviderResolver.Resolve(request.Model)).ChatAsync(request, ct);

    public IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(LlmChatRequest request, CancellationToken ct) =>
        _clientFor(LlmProviderResolver.Resolve(request.Model)).ChatStreamAsync(request, ct);
}
