using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Assistant;

/// <summary>
/// 17-05 (AI-CHAT-01): HTTP-layer tests for the UserId-scoped conversation CRUD — GET /api/ai/conversations
/// (recency list), GET /{id} (with messages), PATCH /{id} (rename), DELETE /{id} (soft-delete). Proves:
/// every route is JWT-authorized (401 without a token, T-17-14); the list returns the caller's rows
/// newest-first; get-one returns the ordered thread; rename/delete mutate then a subsequent get is 404;
/// and the IDOR cases — a conversation owned by another user id is invisible (GET/PATCH/DELETE → 404 with
/// no mutation, T-17-15). Mirrors AiSuggestionEndpointsTests's WebApplicationFactory + InMemory pattern.
/// </summary>
public class ConversationEndpointsTests
{
    [Fact]
    public async Task List_NoAuthToken_Returns401()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ai/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetOne_NoAuthToken_Returns401()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/ai/conversations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rename_NoAuthToken_Returns401()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync($"/api/ai/conversations/{Guid.NewGuid()}", new { title = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_NoAuthToken_Returns401()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/api/ai/conversations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsOwnConversations_NewestFirst()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await AssistantApiFactory.SetupAndLoginAsync(client);

        var older = await SeedConversationAsync(factory, userId, "Older", updatedAt: DateTime.UtcNow.AddHours(-2));
        var newer = await SeedConversationAsync(factory, userId, "Newer", updatedAt: DateTime.UtcNow);
        // A conversation owned by someone else must never appear in this user's list.
        await SeedConversationAsync(factory, Guid.NewGuid(), "Foreign", updatedAt: DateTime.UtcNow);

        var response = await client.SendAsync(Authed(HttpMethod.Get, "/api/ai/conversations", accessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(2, body.GetArrayLength());
        Assert.Equal(newer, Guid.Parse(body[0].GetProperty("id").GetString()!));
        Assert.Equal(older, Guid.Parse(body[1].GetProperty("id").GetString()!));
        Assert.Equal("Newer", body[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetOne_ReturnsConversationWithOrderedMessages()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await AssistantApiFactory.SetupAndLoginAsync(client);

        var conversationId = await SeedConversationAsync(factory, userId, "With messages", updatedAt: DateTime.UtcNow);
        await SeedMessageAsync(factory, userId, conversationId, MessageRole.User, "quanto gastei?", createdAt: DateTime.UtcNow.AddSeconds(-2));
        await SeedMessageAsync(factory, userId, conversationId, MessageRole.Tool, content: null, createdAt: DateTime.UtcNow.AddSeconds(-1), toolName: "sum_by_category");
        await SeedMessageAsync(factory, userId, conversationId, MessageRole.Assistant, "Você gastou R$ 100.", createdAt: DateTime.UtcNow);

        var response = await client.SendAsync(Authed(HttpMethod.Get, $"/api/ai/conversations/{conversationId}", accessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(conversationId, Guid.Parse(body.GetProperty("id").GetString()!));
        var messages = body.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("User", messages[0].GetProperty("role").GetString());
        Assert.Equal("Tool", messages[1].GetProperty("role").GetString());
        Assert.Equal("sum_by_category", messages[1].GetProperty("toolName").GetString());
        Assert.Equal("Assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("Você gastou R$ 100.", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Rename_OwnConversation_Returns204_AndPersists()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await AssistantApiFactory.SetupAndLoginAsync(client);

        var conversationId = await SeedConversationAsync(factory, userId, "Before", updatedAt: DateTime.UtcNow);

        var response = await client.SendAsync(AuthedJson(HttpMethod.Patch, $"/api/ai/conversations/{conversationId}", accessToken, new { title = "After" }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.AiConversations.FindAsync(conversationId);
        Assert.Equal("After", row!.Title);
    }

    [Fact]
    public async Task Rename_BlankTitle_Returns400()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await AssistantApiFactory.SetupAndLoginAsync(client);

        var conversationId = await SeedConversationAsync(factory, userId, "Before", updatedAt: DateTime.UtcNow);

        var response = await client.SendAsync(AuthedJson(HttpMethod.Patch, $"/api/ai/conversations/{conversationId}", accessToken, new { title = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_OwnConversation_Returns204_ThenGetIs404()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await AssistantApiFactory.SetupAndLoginAsync(client);

        var conversationId = await SeedConversationAsync(factory, userId, "To delete", updatedAt: DateTime.UtcNow);

        var delete = await client.SendAsync(Authed(HttpMethod.Delete, $"/api/ai/conversations/{conversationId}", accessToken));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var get = await client.SendAsync(Authed(HttpMethod.Get, $"/api/ai/conversations/{conversationId}", accessToken));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task GetOne_ForeignConversation_Returns404_NoLeak()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await AssistantApiFactory.SetupAndLoginAsync(client);

        var foreignId = await SeedConversationAsync(factory, Guid.NewGuid(), "Someone else", updatedAt: DateTime.UtcNow);

        var response = await client.SendAsync(Authed(HttpMethod.Get, $"/api/ai/conversations/{foreignId}", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rename_ForeignConversation_Returns404_NoMutation()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await AssistantApiFactory.SetupAndLoginAsync(client);

        var foreignId = await SeedConversationAsync(factory, Guid.NewGuid(), "Someone else", updatedAt: DateTime.UtcNow);

        var response = await client.SendAsync(AuthedJson(HttpMethod.Patch, $"/api/ai/conversations/{foreignId}", accessToken, new { title = "hijacked" }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.AiConversations.FindAsync(foreignId);
        Assert.Equal("Someone else", row!.Title);
    }

    [Fact]
    public async Task Delete_ForeignConversation_Returns404_NoMutation()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await AssistantApiFactory.SetupAndLoginAsync(client);

        var foreignId = await SeedConversationAsync(factory, Guid.NewGuid(), "Someone else", updatedAt: DateTime.UtcNow);

        var response = await client.SendAsync(Authed(HttpMethod.Delete, $"/api/ai/conversations/{foreignId}", accessToken));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.AiConversations.FindAsync(foreignId);
        Assert.Null(row!.DeletedAt);
    }

    // ---- helpers ----

    private static async Task<Guid> SeedConversationAsync(
        AssistantApiFactory factory, Guid userId, string title, DateTime updatedAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var conversation = new AiConversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = title,
            CreatedAt = updatedAt.AddMinutes(-5),
            UpdatedAt = updatedAt,
        };

        db.AiConversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    private static async Task SeedMessageAsync(
        AssistantApiFactory factory, Guid userId, Guid conversationId, MessageRole role, string? content,
        DateTime createdAt, string? toolName = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        db.AiMessages.Add(new AiMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConversationId = conversationId,
            Role = role,
            Content = content,
            ToolName = toolName,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static HttpRequestMessage AuthedJson(HttpMethod method, string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}
