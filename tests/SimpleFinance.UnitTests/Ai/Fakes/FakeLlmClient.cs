using System.Runtime.CompilerServices;
using SimpleFinance.Application.Ai;

namespace SimpleFinance.UnitTests.Ai.Fakes;

/// <summary>
/// Key-free <see cref="ILlmClient"/> substitute injected by every later AI test. Returns a canned
/// <see cref="LlmResult"/> (fixed content + a configurable <see cref="TokenUsage"/>, no tool calls)
/// and records the last request for assertions. Reads no API key and makes no network call.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    /// <summary>Content the fake returns from <see cref="ChatAsync"/>. Override per test as needed.</summary>
    public string CannedContent { get; set; } = "canned-response";

    /// <summary>Token usage the fake reports — lets budget/cost tests drive a known token count.</summary>
    public TokenUsage CannedUsage { get; set; } = new(PromptTokens: 10, CompletionTokens: 5);

    /// <summary>The most recent request passed to <see cref="ChatAsync"/> / <see cref="ChatStreamAsync"/>.</summary>
    public LlmChatRequest? LastRequest { get; private set; }

    /// <summary>Number of times <see cref="ChatAsync"/> was invoked.</summary>
    public int ChatCallCount { get; private set; }

    public Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct)
    {
        LastRequest = request;
        ChatCallCount++;
        var result = new LlmResult(
            Content: CannedContent,
            ReasoningContent: null,
            ToolCalls: [],
            Usage: CannedUsage);
        return Task.FromResult(result);
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        LastRequest = request;
        await Task.CompletedTask;
        yield break;
    }
}
