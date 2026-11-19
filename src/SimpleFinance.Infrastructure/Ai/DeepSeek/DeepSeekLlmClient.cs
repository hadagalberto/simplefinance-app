using Microsoft.Extensions.Options;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.OpenAiCompatible;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.DeepSeek;

/// <summary>
/// AI-FND-01: DeepSeek chat over its OpenAI-compatible <c>POST /chat/completions</c> API. All request
/// mapping and the redact → budget-check → send → record invariant live in
/// <see cref="OpenAiCompatibleLlmClient"/>; this class only names the provider and its streaming
/// client. Auth is the per-request <see cref="DeepSeekBearerHandler"/> registered on the typed client.
/// Selected by <c>RoutingLlmClient</c> for any model name that is neither <c>claude-*</c> nor an
/// OpenRouter <c>vendor/model</c> slug (e.g. <c>deepseek-chat</c>, <c>deepseek-reasoner</c>). The
/// deepseek-chat/reasoner names deprecate 2026-07-24 → deepseek-v4-flash (a UserSettings edit).
/// </summary>
public sealed class DeepSeekLlmClient : OpenAiCompatibleLlmClient
{
    /// <summary>Named streaming HttpClient (registered in AddAiClients without the total-request
    /// timeout so long SSE reads are not aborted — RESEARCH Pitfall 3 / T-15-12).</summary>
    public const string StreamingHttpClientName = "DeepSeekStreaming";

    public DeepSeekLlmClient(
        HttpClient httpClient,
        IHttpClientFactory httpClientFactory,
        IOptions<AiOptions> options,
        IPiiRedactor redactor,
        IAiBudgetGuard budgetGuard,
        IAiUsageRecorder usageRecorder,
        SimpleFinanceDbContext db)
        : base(httpClient, httpClientFactory, redactor, budgetGuard, usageRecorder, db)
    {
    }

    protected override AiProvider Provider => AiProvider.DeepSeek;

    protected override string ProviderName => "DeepSeek";

    protected override string StreamingClientName => StreamingHttpClientName;
}
