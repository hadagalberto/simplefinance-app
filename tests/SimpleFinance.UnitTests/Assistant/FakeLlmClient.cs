using System.Runtime.CompilerServices;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;

namespace SimpleFinance.UnitTests.Assistant;

/// <summary>
/// Scriptable <see cref="ILlmClient"/> test double for the orchestrator (17-04). Unlike the minimal
/// <c>Ai/Fakes/FakeLlmClient</c>, this one can:
/// <list type="bullet">
///   <item>return a queue of <see cref="LlmResult"/>s from <see cref="ChatAsync"/> (tool_calls then a
///   final answer);</item>
///   <item>always return the same tool-call result (to exercise the max-iteration guard);</item>
///   <item>throw <see cref="AiBudgetExceededException"/> from <see cref="ChatAsync"/> or
///   <see cref="ChatStreamAsync"/> (budget-degrade path);</item>
///   <item>stream a scripted delta sequence + a final usage chunk from <see cref="ChatStreamAsync"/>;</item>
///   <item>capture every request for assertions (model name, tools enabled/disabled, messages).</item>
/// </list>
/// Reads no API key and makes no network call.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<LlmResult> _chatResults = new();

    /// <summary>When set, every <see cref="ChatAsync"/> call returns this result (ignores the queue).</summary>
    public LlmResult? AlwaysReturn { get; set; }

    /// <summary>When true, <see cref="ChatAsync"/> throws <see cref="AiBudgetExceededException"/>.</summary>
    public bool ThrowBudgetOnChat { get; set; }

    /// <summary>When true, <see cref="ChatStreamAsync"/> throws <see cref="AiBudgetExceededException"/> on first read.</summary>
    public bool ThrowBudgetOnStream { get; set; }

    /// <summary>When true, <see cref="ChatAsync"/> throws a generic provider failure (e.g. 401 bad key).</summary>
    public bool ThrowOnChat { get; set; }

    /// <summary>When true, <see cref="ChatStreamAsync"/> throws a generic provider failure on first read.</summary>
    public bool ThrowOnStream { get; set; }

    /// <summary>Deltas the stream yields as the final prose answer.</summary>
    public IReadOnlyList<string> FinalDeltas { get; set; } = ["Você gastou ", "R$ 1234,56", "."];

    /// <summary>Usage reported on the final stream chunk.</summary>
    public TokenUsage StreamUsage { get; set; } = new(PromptTokens: 120, CompletionTokens: 30);

    /// <summary>Every request passed to <see cref="ChatAsync"/>, in order.</summary>
    public List<LlmChatRequest> ChatRequests { get; } = [];

    /// <summary>The last request passed to <see cref="ChatStreamAsync"/> (the final-answer turn).</summary>
    public LlmChatRequest? LastStreamRequest { get; private set; }

    public int ChatCallCount => ChatRequests.Count;

    public int StreamCallCount { get; private set; }

    public void EnqueueChat(LlmResult result) => _chatResults.Enqueue(result);

    /// <summary>Build a result that asks for a single tool call.</summary>
    public static LlmResult ToolCall(string id, string name, string argsJson, string content = "") =>
        new(content, ReasoningContent: null, ToolCalls: [new LlmToolCall(id, name, argsJson)], Usage: new TokenUsage(60, 10));

    /// <summary>Build a result that asks for several tool calls in a single response (multi-proposal turn).</summary>
    public static LlmResult ToolCalls(params LlmToolCall[] calls) =>
        new(string.Empty, ReasoningContent: null, ToolCalls: [.. calls], Usage: new TokenUsage(60, 10));

    /// <summary>Build a result with no tool calls (the model is ready to answer in prose).</summary>
    public static LlmResult FinalAnswer(string content = "final") =>
        new(content, ReasoningContent: null, ToolCalls: [], Usage: new TokenUsage(60, 10));

    public Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct)
    {
        ChatRequests.Add(request);
        if (ThrowBudgetOnChat)
        {
            throw new AiBudgetExceededException(Guid.Empty, currentSpendUsd: 5m, monthlyBudgetUsd: 3m);
        }

        if (ThrowOnChat)
        {
            throw new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).");
        }

        if (AlwaysReturn is not null)
        {
            return Task.FromResult(AlwaysReturn);
        }

        return Task.FromResult(_chatResults.Dequeue());
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        LastStreamRequest = request;
        StreamCallCount++;
        if (ThrowBudgetOnStream)
        {
            throw new AiBudgetExceededException(Guid.Empty, currentSpendUsd: 5m, monthlyBudgetUsd: 3m);
        }

        if (ThrowOnStream)
        {
            throw new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).");
        }

        foreach (var delta in FinalDeltas)
        {
            yield return new LlmStreamChunk(ContentDelta: delta);
        }

        yield return new LlmStreamChunk(Usage: StreamUsage);
        await Task.CompletedTask;
    }
}
