using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.Assistant;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Assistant;

/// <summary>
/// AI-CHAT-04 (17-06 Task 2): HTTP-layer tests for POST /api/ai/actions/{id}/confirm|cancel — the ONLY
/// paths that execute a staged write proposal. Proves: confirm dispatches to the domain service exactly
/// once (the Goal row now exists) and flips the action Confirmed; a re-confirm is 409 with NO double-write
/// (still exactly one Goal); cancel discards a Pending action (200, no write) and a subsequent confirm is
/// 409; an expired (&gt;24h) action is 409 with no write; a foreign user confirming/cancelling another
/// user's action gets 404 (IDOR-safe, T-17-20); and both routes are JWT-gated (401 without a token).
/// Lives in SimpleFinance.UnitTests (no separate IntegrationTests project exists — the 17-05 convention).
/// </summary>
public class ActionEndpointsTests
{
    [Fact]
    public async Task Confirm_NoAuthToken_Returns401()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/ai/actions/{Guid.NewGuid()}/confirm", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_NoAuthToken_Returns401()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/ai/actions/{Guid.NewGuid()}/cancel", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Confirm_PendingCreateGoal_Returns200_CreatesGoal_Once()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await AssistantApiFactory.SetupAndLoginAsync(client);
        var actionId = await SeedCreateGoalActionAsync(factory, userId, "Viagem", 5000m);

        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/ai/actions/{actionId}/confirm", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Confirmed", body.GetProperty("status").GetString());
        Assert.Equal(1, await CountGoalsAsync(factory, userId));
        Assert.Equal(PendingActionStatus.Confirmed, await GetActionStatusAsync(factory, actionId));
    }

    [Fact]
    public async Task Confirm_Twice_SecondReturns409_NoDoubleWrite()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await AssistantApiFactory.SetupAndLoginAsync(client);
        var actionId = await SeedCreateGoalActionAsync(factory, userId, "Viagem", 5000m);

        var first = await client.SendAsync(Authed(HttpMethod.Post, $"/api/ai/actions/{actionId}/confirm", accessToken));
        var second = await client.SendAsync(Authed(HttpMethod.Post, $"/api/ai/actions/{actionId}/confirm", accessToken));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await CountGoalsAsync(factory, userId)); // still exactly one
    }

    [Fact]
    public async Task Cancel_PendingAction_Returns200_NoWrite_ThenConfirmReturns409()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await AssistantApiFactory.SetupAndLoginAsync(client);
        var actionId = await SeedCreateGoalActionAsync(factory, userId, "Viagem", 5000m);

        var cancel = await client.SendAsync(Authed(HttpMethod.Post, $"/api/ai/actions/{actionId}/cancel", accessToken));
        var confirmAfterCancel = await client.SendAsync(Authed(HttpMethod.Post, $"/api/ai/actions/{actionId}/confirm", accessToken));

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, confirmAfterCancel.StatusCode);
        Assert.Equal(0, await CountGoalsAsync(factory, userId));
        Assert.Equal(PendingActionStatus.Cancelled, await GetActionStatusAsync(factory, actionId));
    }

    [Fact]
    public async Task Confirm_ExpiredAction_Returns409_NoWrite()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await AssistantApiFactory.SetupAndLoginAsync(client);
        var stale = DateTime.UtcNow - AiPendingActionService.ExpiryWindow - TimeSpan.FromMinutes(5);
        var actionId = await SeedCreateGoalActionAsync(factory, userId, "Antiga", 100m, createdAt: stale);

        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/ai/actions/{actionId}/confirm", accessToken));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await CountGoalsAsync(factory, userId));
        Assert.Equal(PendingActionStatus.Expired, await GetActionStatusAsync(factory, actionId));
    }

    [Fact]
    public async Task Confirm_ForeignUsersAction_Returns404_NoWrite()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await AssistantApiFactory.SetupAndLoginAsync(client);
        // Seed an action owned by a DIFFERENT user id (the single-user API can't create a second user).
        var foreignUserId = Guid.NewGuid();
        var actionId = await SeedCreateGoalActionAsync(factory, foreignUserId, "Alheia", 5000m);

        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/ai/actions/{actionId}/confirm", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountGoalsAsync(factory, foreignUserId));
        Assert.Equal(PendingActionStatus.Pending, await GetActionStatusAsync(factory, actionId));
    }

    [Fact]
    public async Task Cancel_ForeignUsersAction_Returns404()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await AssistantApiFactory.SetupAndLoginAsync(client);
        var foreignUserId = Guid.NewGuid();
        var actionId = await SeedCreateGoalActionAsync(factory, foreignUserId, "Alheia", 5000m);

        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/ai/actions/{actionId}/cancel", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(PendingActionStatus.Pending, await GetActionStatusAsync(factory, actionId));
    }

    [Fact]
    public async Task Confirm_UnknownId_Returns404()
    {
        using var factory = new AssistantApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await AssistantApiFactory.SetupAndLoginAsync(client);

        var response = await client.SendAsync(Authed(HttpMethod.Post, $"/api/ai/actions/{Guid.NewGuid()}/confirm", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static async Task<Guid> SeedCreateGoalActionAsync(
        AssistantApiFactory factory, Guid userId, string name, decimal targetAmount, DateTime? createdAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var when = createdAt ?? DateTime.UtcNow;
        var id = Guid.NewGuid();
        db.AiPendingActions.Add(new AiPendingAction
        {
            Id = id,
            UserId = userId,
            ActionType = PendingActionType.CreateGoal,
            Payload = JsonSerializer.Serialize(new { name, targetAmount }),
            Status = PendingActionStatus.Pending,
            CreatedAt = when,
            UpdatedAt = when,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<int> CountGoalsAsync(AssistantApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        return await db.Goals.CountAsync(g => g.UserId == userId);
    }

    private static async Task<PendingActionStatus> GetActionStatusAsync(AssistantApiFactory factory, Guid actionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var action = await db.AiPendingActions.SingleAsync(a => a.Id == actionId);
        return action.Status;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}
