using System.Text.Json;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Application.Ai;

/// <summary>
/// Provider-neutral request/result records consumed by AI callers (later phases:
/// Categorization, Assistant, Summary) and produced by whatever Infrastructure-side client
/// implements <see cref="ILlmClient"/> / <see cref="IEmbeddingClient"/> (15-03). No DeepSeek /
/// OpenAI wire field names leak here — the Infrastructure client maps its own private wire DTOs
/// (e.g. <c>chat/completions</c> JSON) into these records, exactly as ProviderDtos.cs does for
/// Pluggy. A future provider swap never touches these shapes.
/// </summary>
public sealed record LlmChatRequest(
    string Model,
    IReadOnlyList<LlmMessage> Messages,
    AiFeature Feature,
    IReadOnlyList<LlmTool>? Tools = null,
    string? ToolChoice = null,
    bool Stream = false);

/// <summary>
/// One message in a chat exchange. <paramref name="Role"/> is a neutral role name
/// (<c>system</c> | <c>user</c> | <c>assistant</c> | <c>tool</c>). <paramref name="ToolCalls"/> is
/// set on assistant messages that requested tools; <paramref name="ToolCallId"/> is set on tool
/// result messages replying to a specific call.
/// </summary>
public sealed record LlmMessage(
    string Role,
    string? Content,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>
/// A function tool advertised to the model. <paramref name="Parameters"/> is a JSON-Schema
/// document describing the function arguments.
/// </summary>
public sealed record LlmTool(
    string Name,
    string? Description,
    JsonElement Parameters);

/// <summary>
/// A tool invocation the model asked for. <paramref name="ArgumentsJson"/> is the raw JSON
/// arguments string as emitted by the model — callers parse it against the tool schema.
/// </summary>
public sealed record LlmToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

/// <summary>
/// Result of a non-streaming chat call. <paramref name="ReasoningContent"/> is populated only by
/// reasoning models (deepseek-reasoner); <paramref name="ToolCalls"/> is empty when the model
/// answered directly. <paramref name="Usage"/> carries the token counts used for cost/budget.
/// </summary>
public sealed record LlmResult(
    string Content,
    string? ReasoningContent,
    IReadOnlyList<LlmToolCall> ToolCalls,
    TokenUsage Usage);

/// <summary>
/// One incremental chunk of a streamed chat response. Deltas are partial text; the final chunk
/// carries <paramref name="Usage"/> when the provider is asked to include it.
/// </summary>
public sealed record LlmStreamChunk(
    string? ContentDelta = null,
    string? ReasoningDelta = null,
    TokenUsage? Usage = null);

/// <summary>
/// Provider-neutral token accounting for a single call — the input to cost estimation and the
/// monthly budget guard (15-03/15-04).
/// </summary>
public sealed record TokenUsage(
    int PromptTokens,
    int CompletionTokens);
