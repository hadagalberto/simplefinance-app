using System.Text.Json;
using Anthropic.Models.Messages;
using SimpleFinance.Application.Ai;

namespace SimpleFinance.Infrastructure.Ai.Anthropic;

/// <summary>
/// Thrown when Claude ends a turn with <c>stop_reason = "refusal"</c>. Callers already catch any
/// non-budget exception from <see cref="ILlmClient"/> and take their deterministic fallback, so a
/// refusal degrades exactly like a provider outage.
/// </summary>
public sealed class AnthropicRefusalException(string model)
    : InvalidOperationException($"Anthropic model '{model}' declined the request (stop_reason=refusal).");

/// <summary>
/// Pure mapping between the provider-neutral <see cref="LlmChatRequest"/>/<see cref="LlmResult"/>
/// records and the official Anthropic C# SDK types. Kept free of I/O so it is unit-testable without a
/// network. Rules:
/// <list type="bullet">
///   <item><c>system</c> messages → the top-level <c>system</c> parameter (joined with blank lines);</item>
///   <item><c>assistant</c> messages → text block (when non-empty) + one <c>tool_use</c> block per tool call
///   (input = parsed <c>ArgumentsJson</c>, <c>{}</c> when empty/invalid);</item>
///   <item>consecutive <c>tool</c> messages → ONE <c>user</c> message of <c>tool_result</c> blocks
///   (a directly following <c>user</c> turn's text is appended to that same message);</item>
///   <item>tools → name/description/input_schema (the JSON schema passed through whole); tool_choice
///   <c>"none"</c> → none, anything else → auto (never forced — <c>any</c>/<c>tool</c> 400 on current models);</item>
///   <item>no <c>thinking</c>, <c>temperature</c>, <c>top_p</c> or <c>top_k</c> are ever sent.</item>
/// </list>
/// Every outbound text (system, user, assistant, tool result) goes through <see cref="IPiiRedactor"/>.
/// </summary>
public static class AnthropicMessageMapper
{
    /// <summary><c>max_tokens</c> for non-streaming calls.</summary>
    public const long NonStreamingMaxTokens = 16000;

    /// <summary><c>max_tokens</c> for streaming calls.</summary>
    public const long StreamingMaxTokens = 64000;

    public static MessageCreateParams BuildParams(LlmChatRequest request, IPiiRedactor redactor, bool stream)
    {
        var systemParts = new List<string>();
        var messages = new List<MessageParam>();
        var pendingResults = new List<ContentBlockParam>();

        // The API rejects tool_use/tool_result blocks when the request defines no tools (e.g. the
        // assistant's final tools-disabled answer after the tool loop). In that case the exchange is
        // flattened to plain text so the context survives.
        var hasTools = request.Tools is { Count: > 0 };

        void FlushResults()
        {
            if (pendingResults.Count > 0)
            {
                messages.Add(new UserMessageParam { Content = new List<ContentBlockParam>(pendingResults) });
                pendingResults.Clear();
            }
        }

        foreach (var m in request.Messages)
        {
            var text = redactor.Redact(m.Content);
            switch (m.Role)
            {
                case "system":
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        systemParts.Add(text);
                    }

                    break;

                case "tool":
                    if (hasTools)
                    {
                        pendingResults.Add(new ToolResultBlockParam
                        {
                            ToolUseID = m.ToolCallId ?? string.Empty,
                            Content = text ?? string.Empty,
                        });
                    }
                    else
                    {
                        pendingResults.Add(new TextBlockParam { Text = $"[tool result] {text}" });
                    }

                    break;

                case "assistant":
                {
                    FlushResults();
                    if (messages.Count == 0)
                    {
                        // History truncation can leave an assistant turn first; the conversation must
                        // open with a user turn, so a leading assistant turn is dropped.
                        break;
                    }

                    var blocks = new List<ContentBlockParam>();
                    if (!string.IsNullOrEmpty(text))
                    {
                        blocks.Add(new TextBlockParam { Text = text });
                    }

                    foreach (var call in m.ToolCalls ?? [])
                    {
                        if (hasTools)
                        {
                            blocks.Add(new ToolUseBlockParam
                            {
                                ID = call.Id,
                                Name = call.Name,
                                Input = ParseArguments(call.ArgumentsJson),
                            });
                        }
                        else
                        {
                            blocks.Add(new TextBlockParam { Text = $"[tool call] {call.Name}({call.ArgumentsJson})" });
                        }
                    }

                    if (blocks.Count > 0)
                    {
                        messages.Add(new AssistantMessageParam { Content = blocks });
                    }

                    break;
                }

                default: // "user" (and any unknown role) → user turn
                    if (pendingResults.Count > 0)
                    {
                        // tool_result blocks must lead the user message; the user's text may follow.
                        if (!string.IsNullOrEmpty(text))
                        {
                            pendingResults.Add(new TextBlockParam { Text = text });
                        }

                        FlushResults();
                    }
                    else
                    {
                        messages.Add(new UserMessageParam { Content = text ?? string.Empty });
                    }

                    break;
            }
        }

        FlushResults();

        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = stream ? StreamingMaxTokens : NonStreamingMaxTokens,
            Messages = messages,
        };

        if (systemParts.Count > 0)
        {
            parameters = parameters with { System = string.Join("\n\n", systemParts) };
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            parameters = parameters with
            {
                Tools = tools.Select(t => (ToolUnion)new Tool
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = ToInputSchema(t.Parameters),
                }).ToList(),
                ToolChoice = string.Equals(request.ToolChoice, "none", StringComparison.OrdinalIgnoreCase)
                    ? new ToolChoiceNone()
                    : new ToolChoiceAuto(),
            };
        }

        return parameters;
    }

    /// <summary>
    /// Maps a Claude response to the neutral result: text blocks concatenated into Content, tool_use
    /// blocks → <see cref="LlmToolCall"/> (input re-serialized to JSON), thinking blocks ignored
    /// (ReasoningContent = null). Throws <see cref="AnthropicRefusalException"/> on a refusal.
    /// </summary>
    public static LlmResult MapResponse(Message message, string model)
    {
        if (IsRefusal(message.StopReason?.Raw()))
        {
            throw new AnthropicRefusalException(model);
        }

        var usage = ToUsage(message);

        var text = new System.Text.StringBuilder();
        var toolCalls = new List<LlmToolCall>();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var textBlock))
            {
                text.Append(textBlock.Text);
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                toolCalls.Add(new LlmToolCall(toolUse.ID, toolUse.Name, JsonSerializer.Serialize(toolUse.Input)));
            }
        }

        return new LlmResult(text.ToString(), ReasoningContent: null, toolCalls, usage);
    }

    /// <summary>Token usage of a response (input_tokens / output_tokens).</summary>
    public static TokenUsage ToUsage(Message message) =>
        new((int)message.Usage.InputTokens, (int)message.Usage.OutputTokens);

    /// <summary>True when the wire <c>stop_reason</c> is <c>refusal</c>.</summary>
    public static bool IsRefusal(string? stopReason) =>
        string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, JsonElement> ParseArguments(string? argumentsJson)
    {
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return doc.RootElement.EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value.Clone());
                }
            }
            catch (JsonException)
            {
                // fall through to {} — a malformed echo must not break the follow-up turn.
            }
        }

        return [];
    }

    private static InputSchema ToInputSchema(JsonElement parameters)
    {
        var raw = parameters.ValueKind == JsonValueKind.Object
            ? parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : [];

        if (!raw.ContainsKey("type"))
        {
            raw["type"] = JsonSerializer.SerializeToElement("object");
        }

        return InputSchema.FromRawUnchecked(raw);
    }
}
