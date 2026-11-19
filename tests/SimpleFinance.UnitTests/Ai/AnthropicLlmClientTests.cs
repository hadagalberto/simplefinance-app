using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Ai;
using SimpleFinance.Application.Ai.Usage;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.Anthropic;
using SimpleFinance.Infrastructure.Persistence;
using static SimpleFinance.UnitTests.Ai.ProviderClientTestSupport;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Anthropic (Claude) client over the official SDK, driven through a fake HttpMessageHandler (no
/// network). Asserts on the wire JSON the SDK emits: system extraction, tool_use echo, consecutive
/// tool results grouped into ONE user message, no thinking/sampling params, PII redaction (incl. tool
/// results), tool schema + tool_choice mapping, refusal → throws, usage recorded as Anthropic,
/// streaming deltas + final usage, missing key → 401-style failure with zero HTTP.
/// </summary>
public class AnthropicLlmClientTests
{
    private const string Model = "claude-sonnet-5-5";

    private const string TextAndToolResponse = """
        {
          "id": "msg_1", "type": "message", "role": "assistant", "model": "claude-sonnet-5-5",
          "content": [
            { "type": "thinking", "thinking": "", "signature": "sig" },
            { "type": "text", "text": "Vou consultar " },
            { "type": "text", "text": "o saldo." },
            { "type": "tool_use", "id": "toolu_1", "name": "get_balance", "input": { "month": "2026-10" } }
          ],
          "stop_reason": "tool_use", "stop_sequence": null,
          "usage": { "input_tokens": 120, "output_tokens": 34 }
        }
        """;

    private const string RefusalResponse = """
        {
          "id": "msg_2", "type": "message", "role": "assistant", "model": "claude-sonnet-5-5",
          "content": [],
          "stop_reason": "refusal", "stop_sequence": null,
          "usage": { "input_tokens": 10, "output_tokens": 1 }
        }
        """;

    private static IOptions<AiOptions> Options() => Microsoft.Extensions.Options.Options.Create(new AiOptions
    {
        Anthropic = new AiProviderEndpoint { BaseUrl = "https://api.anthropic.com" },
    });

    private static AnthropicLlmClient CreateClient(
        CapturingHandler handler,
        SimpleFinanceDbContext db,
        IAiBudgetGuard guard,
        RecordingUsageRecorder recorder,
        string? key = "test-anthropic-key") =>
        new(new SingleClientFactory(handler), new StubKeyProvider(key), Options(), new PiiRedactor(), guard, recorder, db)
        {
            MaxRetries = 0,
        };

    private static LlmChatRequest SimpleRequest(string content = "Olá") =>
        new(Model, [new LlmMessage("system", "Você é um assistente."), new LlmMessage("user", content)], AiFeature.Assistant);

    private static LlmTool BalanceTool() => new(
        "get_balance",
        "Saldo do mês",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { month = new { type = "string" } },
            required = new[] { "month" },
            additionalProperties = false,
        }));

    [Fact]
    public async Task ChatAsync_MapsTextAndToolUse_IgnoresThinking_AndRecordsAnthropicUsage()
    {
        using var handler = new CapturingHandler(TextAndToolResponse);
        await using var db = CreateDbWithUser();
        var recorder = new RecordingUsageRecorder();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), recorder);

        var result = await client.ChatAsync(SimpleRequest(), CancellationToken.None);

        Assert.Equal("Vou consultar o saldo.", result.Content);
        Assert.Null(result.ReasoningContent);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("get_balance", call.Name);
        Assert.Equal("2026-10", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("month").GetString());
        Assert.Equal(new TokenUsage(120, 34), result.Usage);

        Assert.Equal(1, recorder.CallCount);
        Assert.Equal(AiProvider.Anthropic, recorder.LastProvider);
        Assert.Equal(Model, recorder.LastModel);
        Assert.Equal(120, recorder.LastPromptTokens);
        Assert.Equal(34, recorder.LastCompletionTokens);

        Assert.Equal("https://api.anthropic.com/v1/messages", handler.LastRequest!.RequestUri!.ToString());
        // The SDK owns the auth header shape (x-api-key or Bearer, by SDK version) — assert the key we
        // resolved per call is what it sent.
        var sentKey = handler.LastRequest.Headers.TryGetValues("x-api-key", out var apiKeys)
            ? Assert.Single(apiKeys)
            : handler.LastRequest.Headers.Authorization?.Parameter;
        Assert.Equal("test-anthropic-key", sentKey);
    }

    [Fact]
    public async Task ChatAsync_WireBody_SystemTopLevel_NoThinkingOrSamplingParams_MaxTokens16000()
    {
        using var handler = new CapturingHandler(TextAndToolResponse);
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        await client.ChatAsync(SimpleRequest(), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.Equal(16000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("Você é um assistente.", root.GetProperty("system").GetString());
        foreach (var forbidden in new[] { "thinking", "temperature", "top_p", "top_k", "tools", "tool_choice" })
        {
            Assert.False(root.TryGetProperty(forbidden, out _), $"unexpected '{forbidden}' in body");
        }

        var messages = root.GetProperty("messages");
        var only = Assert.Single(messages.EnumerateArray());
        Assert.Equal("user", only.GetProperty("role").GetString());
        Assert.Equal("Olá", only.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatAsync_GroupsConsecutiveToolResultsIntoOneUserMessage_AndEchoesToolUse()
    {
        using var handler = new CapturingHandler(TextAndToolResponse);
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        var request = new LlmChatRequest(
            Model,
            [
                new LlmMessage("system", "Regra A"),
                new LlmMessage("system", "Regra B"),
                new LlmMessage("user", "Quanto gastei?"),
                new LlmMessage("assistant", null, [
                    new LlmToolCall("toolu_a", "get_balance", "{\"month\":\"2026-10\"}"),
                    new LlmToolCall("toolu_b", "get_spend", ""),
                ]),
                new LlmMessage("tool", "{\"balance\":10}", ToolCallId: "toolu_a"),
                new LlmMessage("tool", "Pagamento CPF 123.456.789-09", ToolCallId: "toolu_b"),
                new LlmMessage("assistant", "Você gastou 10."),
                new LlmMessage("user", "Obrigado"),
            ],
            AiFeature.Assistant,
            Tools: [BalanceTool()],
            ToolChoice: "auto");

        await client.ChatAsync(request, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("Regra A\n\nRegra B", root.GetProperty("system").GetString());

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user", "assistant", "user"], messages.Select(m => m.GetProperty("role").GetString()));

        // assistant tool_use echo — null content ⇒ no empty text block
        var toolUses = messages[1].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, toolUses.Count);
        Assert.All(toolUses, b => Assert.Equal("tool_use", b.GetProperty("type").GetString()));
        Assert.Equal("toolu_a", toolUses[0].GetProperty("id").GetString());
        Assert.Equal("2026-10", toolUses[0].GetProperty("input").GetProperty("month").GetString());
        Assert.Equal(JsonValueKind.Object, toolUses[1].GetProperty("input").ValueKind);

        // both tool results in ONE user message, redacted
        var results = messages[2].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, b => Assert.Equal("tool_result", b.GetProperty("type").GetString()));
        Assert.Equal("toolu_a", results[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("toolu_b", results[1].GetProperty("tool_use_id").GetString());
        Assert.DoesNotContain("123.456.789", handler.LastRequestBody);

        // tools + tool_choice auto
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("get_balance", tool.GetProperty("name").GetString());
        Assert.Equal("Saldo do mês", tool.GetProperty("description").GetString());
        var schema = tool.GetProperty("input_schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("month", schema.GetProperty("required")[0].GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ChatAsync_ToolChoiceNone_MapsToNone_AndToolResultFollowedByUserText_SharesOneMessage()
    {
        using var handler = new CapturingHandler(TextAndToolResponse);
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        var request = new LlmChatRequest(
            Model,
            [
                new LlmMessage("user", "Oi"),
                new LlmMessage("assistant", "Consultando", [new LlmToolCall("toolu_a", "get_balance", "{}")]),
                new LlmMessage("tool", "ok", ToolCallId: "toolu_a"),
                new LlmMessage("user", "Resuma"),
            ],
            AiFeature.Assistant,
            Tools: [BalanceTool()],
            ToolChoice: "none");

        await client.ChatAsync(request, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("none", root.GetProperty("tool_choice").GetProperty("type").GetString());
        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, messages.Count);
        var assistantBlocks = messages[1].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["text", "tool_use"], assistantBlocks.Select(b => b.GetProperty("type").GetString()));
        var lastBlocks = messages[2].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["tool_result", "text"], lastBlocks.Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("Resuma", lastBlocks[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ChatAsync_RedactsPii_InUserText()
    {
        using var handler = new CapturingHandler(TextAndToolResponse);
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        await client.ChatAsync(SimpleRequest("Pagamento para CPF 123.456.789-09 confirmado"), CancellationToken.None);

        Assert.DoesNotContain("123.456.789", handler.LastRequestBody);
        Assert.Contains("REDACTED", handler.LastRequestBody);
    }

    [Fact]
    public async Task ChatAsync_Refusal_Throws_SoCallersFallBack()
    {
        using var handler = new CapturingHandler(RefusalResponse);
        await using var db = CreateDbWithUser();
        var recorder = new RecordingUsageRecorder();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), recorder);

        await Assert.ThrowsAsync<AnthropicRefusalException>(() => client.ChatAsync(SimpleRequest(), CancellationToken.None));
        Assert.Equal(1, recorder.CallCount); // the refused call still counts toward the budget
    }

    [Fact]
    public async Task ChatAsync_WithoutTools_FlattensToolHistoryToText_AndDropsLeadingAssistantTurn()
    {
        using var handler = new CapturingHandler(TextAndToolResponse);
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder());

        // Shape of the assistant's final tools-disabled answer after a truncated history.
        var request = new LlmChatRequest(
            Model,
            [
                new LlmMessage("system", "Regras"),
                new LlmMessage("assistant", "Resposta antiga"),
                new LlmMessage("user", "Quanto gastei?"),
                new LlmMessage("assistant", null, [new LlmToolCall("toolu_a", "get_balance", "{\"month\":\"2026-10\"}")]),
                new LlmMessage("tool", "{\"balance\":10}", ToolCallId: "toolu_a"),
            ],
            AiFeature.Assistant);

        await client.ChatAsync(request, CancellationToken.None);

        Assert.DoesNotContain("tool_use", handler.LastRequestBody);
        Assert.DoesNotContain("tool_result", handler.LastRequestBody);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("Quanto gastei?", messages[0].GetProperty("content").GetString());
        Assert.Contains("get_balance", messages[1].GetRawText());
        Assert.Contains("balance", messages[2].GetRawText());
    }

    [Fact]
    public async Task ChatAsync_WhenBudgetGuardThrows_MakesZeroHttpCalls()
    {
        using var handler = new CapturingHandler(TextAndToolResponse);
        await using var db = CreateDbWithUser();
        var recorder = new RecordingUsageRecorder();
        var client = CreateClient(handler, db, new ThrowingBudgetGuard(), recorder);

        await Assert.ThrowsAsync<AiBudgetExceededException>(() => client.ChatAsync(SimpleRequest(), CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
        Assert.Equal(0, recorder.CallCount);
    }

    [Fact]
    public async Task ChatAsync_WithoutKey_Fails401Style_WithZeroHttpCalls()
    {
        using var handler = new CapturingHandler(TextAndToolResponse);
        await using var db = CreateDbWithUser();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), new RecordingUsageRecorder(), key: null);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync(SimpleRequest(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ChatStreamAsync_YieldsTextDeltas_ThenUsage_AndRecordsIt()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_s","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":50,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Bom "}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"dia"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":12}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        using var handler = new CapturingHandler(sse, "text/event-stream");
        await using var db = CreateDbWithUser();
        var recorder = new RecordingUsageRecorder();
        var client = CreateClient(handler, db, new PassThroughBudgetGuard(), recorder);

        var text = "";
        TokenUsage? usage = null;
        await foreach (var chunk in client.ChatStreamAsync(SimpleRequest(), CancellationToken.None))
        {
            text += chunk.ContentDelta;
            usage ??= chunk.Usage;
        }

        Assert.Equal("Bom dia", text);
        Assert.Equal(new TokenUsage(50, 12), usage);
        Assert.Equal(1, recorder.CallCount);
        Assert.Equal(AiProvider.Anthropic, recorder.LastProvider);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(64000, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
    }
}
