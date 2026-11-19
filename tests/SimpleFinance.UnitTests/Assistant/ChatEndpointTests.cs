using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Assistant;

/// <summary>
/// 17-05 (AI-CHAT-01): end-to-end tests for the streaming chat endpoint POST /api/ai/chat. With a
/// scripted <see cref="FakeLlmClient"/> (one tool_call then a final answer) it asserts the response is
/// <c>text/event-stream</c>, that a <c>tool</c> chip + <c>token</c> deltas + a terminal <c>done</c> frame
/// arrive in order (the grounding acceptance flow tool→answer for "quanto gastei com Alimentação em
/// maio?"), and that the assistant turn is persisted as an AiMessage. A budget-throwing fake degrades to
/// an <c>error</c> frame (never a 500, T-17-17); a foreign conversation id is a 404 before any streaming
/// (T-17-15); and an unauthenticated POST is a 401 (T-17-14).
/// </summary>
public class ChatEndpointTests
{
    private const string SumByCategoryArgs = """{"period":"2026-05","type":"expense"}""";

    [Fact]
    public async Task Post_NoAuthToken_Returns401()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Json(HttpMethod.Post, "/api/ai/chat", accessToken: null,
            new { conversationId = (Guid?)null, message = "oi" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_BlankMessage_Returns400()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await AssistantApiFactory.SetupAndLoginAsync(client);

        var response = await client.SendAsync(Json(HttpMethod.Post, "/api/ai/chat", accessToken,
            new { conversationId = (Guid?)null, message = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_ForeignConversationId_Returns404_NoStreaming()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await AssistantApiFactory.SetupAndLoginAsync(client);

        // A conversation id that resolves to nothing for this user must 404 BEFORE the orchestrator runs.
        var response = await client.SendAsync(Json(HttpMethod.Post, "/api/ai/chat", accessToken,
            new { conversationId = Guid.NewGuid(), message = "quanto gastei?" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, factory.Llm.ChatCallCount); // orchestrator never invoked
    }

    [Fact]
    public async Task Post_ToolThenAnswer_StreamsSseFrames_AndPersistsAssistantMessage()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await AssistantApiFactory.SetupAndLoginAsync(client);

        // Iteration 0: the model asks for sum_by_category. Iteration 1: no tool calls → stream the answer.
        factory.Llm.EnqueueChat(FakeLlmClient.ToolCall("call-1", "sum_by_category", SumByCategoryArgs));
        factory.Llm.EnqueueChat(FakeLlmClient.FinalAnswer("ready-to-stream"));
        factory.Llm.FinalDeltas = ["Você gastou ", "R$ 1234,56", " com Alimentação em maio."];

        var response = await client.SendAsync(Json(HttpMethod.Post, "/api/ai/chat", accessToken,
            new { conversationId = (Guid?)null, message = "quanto gastei com Alimentação em maio?" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        var frames = ParseSse(await response.Content.ReadAsStringAsync());

        // A transparency chip named for the invoked tool.
        var toolFrame = Assert.Single(frames, f => f.Event == "tool");
        Assert.Equal("sum_by_category", toolFrame.Data.GetProperty("toolName").GetString());

        // The prose answer streamed as token deltas.
        var tokenFrames = frames.Where(f => f.Event == "token").ToList();
        Assert.NotEmpty(tokenFrames);
        var streamed = string.Concat(tokenFrames.Select(f => f.Data.GetProperty("text").GetString()));
        Assert.Equal("Você gastou R$ 1234,56 com Alimentação em maio.", streamed);

        // Exactly one terminal done frame carrying the persisted message id.
        var doneFrame = Assert.Single(frames, f => f.Event == "done");
        var messageId = Guid.Parse(doneFrame.Data.GetProperty("messageId").GetString()!);
        Assert.NotEqual(Guid.Empty, messageId);

        // The assistant answer is persisted as an AiMessage row for this user.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var assistant = await db.AiMessages
            .SingleAsync(m => m.Id == messageId && m.UserId == userId);
        Assert.Equal(MessageRole.Assistant, assistant.Role);
        Assert.Equal("Você gastou R$ 1234,56 com Alimentação em maio.", assistant.Content);
    }

    [Fact]
    public async Task Post_BudgetExceeded_EmitsErrorFrame_NotA500()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await AssistantApiFactory.SetupAndLoginAsync(client);

        factory.Llm.ThrowBudgetOnChat = true;

        var response = await client.SendAsync(Json(HttpMethod.Post, "/api/ai/chat", accessToken,
            new { conversationId = (Guid?)null, message = "quanto gastei?" }));

        // The SSE response starts 200; the cap surfaces as an error frame, never an unhandled 500.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        var frames = ParseSse(await response.Content.ReadAsStringAsync());
        var errorFrame = Assert.Single(frames, f => f.Event == "error");
        Assert.False(string.IsNullOrWhiteSpace(errorFrame.Data.GetProperty("text").GetString()));
        Assert.DoesNotContain(frames, f => f.Event == "done");
    }

    // ---- SSE parsing ----

    private readonly record struct SseFrame(string? Event, JsonElement Data);

    /// <summary>
    /// Parse a completed SSE body into its frames. Frames are blank-line separated; each has an
    /// <c>event:</c> line and a JSON <c>data:</c> line (the serialized ChatEvent).
    /// </summary>
    private static List<SseFrame> ParseSse(string body)
    {
        var frames = new List<SseFrame>();
        string? currentEvent = null;
        string? currentData = null;

        foreach (var rawLine in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                if (currentData is not null)
                {
                    frames.Add(new SseFrame(currentEvent, JsonSerializer.Deserialize<JsonElement>(currentData)));
                }

                currentEvent = null;
                currentData = null;
                continue;
            }

            if (rawLine.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = rawLine["event:".Length..].Trim();
            }
            else if (rawLine.StartsWith("data:", StringComparison.Ordinal))
            {
                currentData = rawLine["data:".Length..].Trim();
            }
        }

        if (currentData is not null)
        {
            frames.Add(new SseFrame(currentEvent, JsonSerializer.Deserialize<JsonElement>(currentData)));
        }

        return frames;
    }

    private static HttpRequestMessage Json(HttpMethod method, string path, string? accessToken, object payload)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }
}
