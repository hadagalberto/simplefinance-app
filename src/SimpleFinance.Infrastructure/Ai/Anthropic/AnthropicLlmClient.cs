using System.Net;
using System.Runtime.CompilerServices;
using Anthropic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai.Anthropic;

/// <summary>
/// <see cref="ILlmClient"/> over the Anthropic Messages API (Claude) using the official Anthropic C#
/// SDK. Selected by <c>RoutingLlmClient</c> for <c>claude-*</c> model names. Same un-bypassable
/// invariant as the OpenAI-compatible clients: <b>redact → budget-check → send → record</b> — every
/// outbound text is redacted inside <see cref="AnthropicMessageMapper.BuildParams"/>, the budget guard
/// runs before any HTTP, and usage is recorded as <see cref="AiProvider.Anthropic"/> after the response.
///
/// The key is resolved per call through <see cref="IAiKeyProvider"/> (DB-decrypt-else-env); with no key
/// the call fails with a 401-style <see cref="HttpRequestException"/> before anything is sent — the same
/// exception type DeepSeek surfaces when its key is missing. The SDK client is built per call over a
/// named HttpClient without the standard resilience handler (the SDK retries on its own, and the
/// resilience timeouts would cut off long thinking/streaming calls).
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    /// <summary>Named HttpClient handed to the SDK (no resilience handler, no client-level timeout).</summary>
    public const string HttpClientName = "Anthropic";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAiKeyProvider _keyProvider;
    private readonly IPiiRedactor _redactor;
    private readonly IAiBudgetGuard _budgetGuard;
    private readonly IAiUsageRecorder _usageRecorder;
    private readonly SimpleFinanceDbContext _db;
    private readonly AiOptions _options;

    public AnthropicLlmClient(
        IHttpClientFactory httpClientFactory,
        IAiKeyProvider keyProvider,
        IOptions<AiOptions> options,
        IPiiRedactor redactor,
        IAiBudgetGuard budgetGuard,
        IAiUsageRecorder usageRecorder,
        SimpleFinanceDbContext db)
    {
        _httpClientFactory = httpClientFactory;
        _keyProvider = keyProvider;
        _redactor = redactor;
        _budgetGuard = budgetGuard;
        _usageRecorder = usageRecorder;
        _db = db;
        _options = options.Value;
    }

    /// <summary>SDK retry count. Tests set 0 so a canned error response is not replayed.</summary>
    public int MaxRetries { get; init; } = 2;

    public async Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);

        // 1. redact (inside BuildParams), then 2. budget-check — both BEFORE any HTTP send.
        var parameters = AnthropicMessageMapper.BuildParams(request, _redactor, stream: false);
        await _budgetGuard.EnsureWithinBudgetAsync(userId, ct);

        // 3. send
        var client = await CreateClientAsync(ct);
        var message = await client.Messages.Create(parameters, ct);

        // 4. record usage — before mapping, so a refusal (which throws) is still billed to the budget.
        var usage = AnthropicMessageMapper.ToUsage(message);
        await _usageRecorder.RecordAsync(
            userId,
            request.Feature,
            AiProvider.Anthropic,
            request.Model,
            usage.PromptTokens,
            usage.CompletionTokens,
            ct);

        return AnthropicMessageMapper.MapResponse(message, request.Model);
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);

        var parameters = AnthropicMessageMapper.BuildParams(request, _redactor, stream: true);
        await _budgetGuard.EnsureWithinBudgetAsync(userId, ct);

        var client = await CreateClientAsync(ct);

        long inputTokens = 0;
        long outputTokens = 0;
        var sawUsage = false;
        string? stopReason = null;

        await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, ct).WithCancellation(ct))
        {
            if (streamEvent.TryPickStart(out var start))
            {
                inputTokens = start.Message.Usage.InputTokens;
                outputTokens = start.Message.Usage.OutputTokens;
                sawUsage = true;
            }
            else if (streamEvent.TryPickContentBlockDelta(out var blockDelta)
                && blockDelta.Delta.TryPickText(out var textDelta)
                && !string.IsNullOrEmpty(textDelta.Text))
            {
                yield return new LlmStreamChunk(ContentDelta: textDelta.Text);
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                // message_delta usage is cumulative; input_tokens is optional here.
                outputTokens = messageDelta.Usage.OutputTokens;
                if (messageDelta.Usage.InputTokens is { } deltaInput)
                {
                    inputTokens = deltaInput;
                }

                sawUsage = true;
                stopReason = messageDelta.Delta.StopReason?.Raw();
            }
        }

        if (sawUsage)
        {
            var usage = new TokenUsage((int)inputTokens, (int)outputTokens);
            await _usageRecorder.RecordAsync(
                userId,
                request.Feature,
                AiProvider.Anthropic,
                request.Model,
                usage.PromptTokens,
                usage.CompletionTokens,
                ct);

            if (AnthropicMessageMapper.IsRefusal(stopReason))
            {
                throw new AnthropicRefusalException(request.Model);
            }

            yield return new LlmStreamChunk(Usage: usage);
        }
        else if (AnthropicMessageMapper.IsRefusal(stopReason))
        {
            throw new AnthropicRefusalException(request.Model);
        }
    }

    private async Task<AnthropicClient> CreateClientAsync(CancellationToken ct)
    {
        var key = await _keyProvider.GetKeyAsync(AiProvider.Anthropic, ct);
        if (string.IsNullOrEmpty(key))
        {
            throw new HttpRequestException(
                "Anthropic API key is not configured (Settings → IA or Ai__AnthropicApiKey).",
                inner: null,
                statusCode: HttpStatusCode.Unauthorized);
        }

        return new AnthropicClient
        {
            ApiKey = key,
            BaseUrl = _options.Anthropic.BaseUrl,
            HttpClient = _httpClientFactory.CreateClient(HttpClientName),
            MaxRetries = MaxRetries,
        };
    }

    /// <summary>Single-user app: the one owning UserId (oldest-first), for budget/usage scoping.</summary>
    private async Task<Guid> ResolveUserIdAsync(CancellationToken ct)
    {
        var userId = await _db.Users
            .OrderBy(u => u.CreatedAt)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(ct);

        if (userId == Guid.Empty)
        {
            throw new InvalidOperationException("No application user is configured; complete first-run setup before using AI features.");
        }

        return userId;
    }
}
