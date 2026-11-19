using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.OpenAiCompatible;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.OpenRouter;

/// <summary>
/// OpenRouter chat over its OpenAI-compatible <c>POST /chat/completions</c> API
/// (<c>https://openrouter.ai/api/v1/</c>). Shares all mapping and the redact → budget-check → send →
/// record invariant with DeepSeek via <see cref="OpenAiCompatibleLlmClient"/>. Selected by
/// <c>RoutingLlmClient</c> for any model name containing <c>/</c> (OpenRouter's <c>vendor/model</c>
/// slugs). Auth is the per-request <see cref="OpenRouterBearerHandler"/>; no attribution headers
/// (HTTP-Referer / X-Title) are sent. Unlike DeepSeek, null fields are omitted from the body so no
/// upstream provider behind OpenRouter sees e.g. an explicit <c>"tool_choice": null</c>.
/// </summary>
public sealed class OpenRouterLlmClient : OpenAiCompatibleLlmClient
{
    /// <summary>Named relaxed-timeout HttpClient for OpenRouter SSE (no resilience handler).</summary>
    public const string StreamingHttpClientName = "OpenRouterStreaming";

    private static readonly JsonSerializerOptions OmitNulls = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public OpenRouterLlmClient(
        HttpClient httpClient,
        IHttpClientFactory httpClientFactory,
        IPiiRedactor redactor,
        IAiBudgetGuard budgetGuard,
        IAiUsageRecorder usageRecorder,
        SimpleFinanceDbContext db)
        : base(httpClient, httpClientFactory, redactor, budgetGuard, usageRecorder, db)
    {
    }

    protected override AiProvider Provider => AiProvider.OpenRouter;

    protected override string ProviderName => "OpenRouter";

    protected override string StreamingClientName => StreamingHttpClientName;

    protected override JsonSerializerOptions? SerializerOptions => OmitNulls;
}
