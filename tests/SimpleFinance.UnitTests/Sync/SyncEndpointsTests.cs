using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Sync;

/// <summary>
/// SYNC-03: manual sync (per-connection + global) is authenticated (T-02-17), enqueues via the
/// SAME ISyncReconciler every other trigger uses (SyncTrigger.Manual), and server-side debounces
/// rapid repeat calls (T-02-16) by checking the most recent SyncLog row against a configurable
/// cooldown window. Mirrors AuthEndpointsTests's real-setup+login-for-JWT pattern and
/// WebhookEndpointTests's FakeBackgroundJobClient swap-in, since real Hangfire/Postgres storage
/// is never available under the "Testing" environment.
/// </summary>
public class SyncEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task PerConnectionSync_NoAuth_Returns401()
    {
        using var factory = new SyncApiFactory();
        using var client = factory.CreateClient();

        var connection = await SeedConnectionAsync(factory);

        var response = await client.PostAsync($"/api/connections/{connection.Id}/sync", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.BackgroundJobClient.CreatedJobs);
    }

    [Fact]
    public async Task GlobalSync_NoAuth_Returns401()
    {
        using var factory = new SyncApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/connections/sync", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.BackgroundJobClient.CreatedJobs);
    }

    [Fact]
    public async Task PerConnectionSync_MissingConnection_Returns404()
    {
        using var factory = new SyncApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost($"/api/connections/{Guid.NewGuid()}/sync", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.BackgroundJobClient.CreatedJobs);
    }

    [Fact]
    public async Task PerConnectionSync_NoRecentSyncLog_Returns202_EnqueuesManualTriggerJob()
    {
        using var factory = new SyncApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory);

        var response = await client.SendAsync(AuthedPost($"/api/connections/{connection.Id}/sync", accessToken));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var enqueued = Assert.Single(factory.BackgroundJobClient.CreatedJobs);
        Assert.Equal("SyncConnectionAsync", enqueued.Method.Name);
        Assert.Equal(connection.Id, enqueued.Args[0]);
        Assert.Equal(SyncTrigger.Manual, enqueued.Args[1]);
    }

    [Fact]
    public async Task PerConnectionSync_RecentSyncLogWithinCooldown_Returns429_DoesNotEnqueueSecondJob()
    {
        using var factory = new SyncApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory);
        await SeedSyncLogAsync(factory, connection.Id, DateTime.UtcNow.AddSeconds(-5));

        var response = await client.SendAsync(AuthedPost($"/api/connections/{connection.Id}/sync", accessToken));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Empty(factory.BackgroundJobClient.CreatedJobs);
    }

    [Fact]
    public async Task PerConnectionSync_OldSyncLogPastCooldown_Returns202_EnqueuesJob()
    {
        using var factory = new SyncApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory);
        await SeedSyncLogAsync(factory, connection.Id, DateTime.UtcNow.AddSeconds(-120));

        var response = await client.SendAsync(AuthedPost($"/api/connections/{connection.Id}/sync", accessToken));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(factory.BackgroundJobClient.CreatedJobs);
    }

    [Fact]
    public async Task GlobalSync_MixedCooldownStates_ReturnsEnqueuedAndSkippedCounts()
    {
        using var factory = new SyncApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var neverSynced = await SeedConnectionAsync(factory, "item-never-synced");
        var recentlySynced = await SeedConnectionAsync(factory, "item-recently-synced");
        await SeedSyncLogAsync(factory, recentlySynced.Id, DateTime.UtcNow.AddSeconds(-5));
        var staleSynced = await SeedConnectionAsync(factory, "item-stale-synced");
        await SeedSyncLogAsync(factory, staleSynced.Id, DateTime.UtcNow.AddSeconds(-120));

        var response = await client.SendAsync(AuthedPost("/api/connections/sync", accessToken));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("enqueued").GetInt32());
        Assert.Equal(1, body.GetProperty("skipped").GetInt32());

        Assert.Equal(2, factory.BackgroundJobClient.CreatedJobs.Count);
        var enqueuedConnectionIds = factory.BackgroundJobClient.CreatedJobs
            .Select(j => (Guid)j.Args[0])
            .ToList();
        Assert.Contains(neverSynced.Id, enqueuedConnectionIds);
        Assert.Contains(staleSynced.Id, enqueuedConnectionIds);
        Assert.DoesNotContain(recentlySynced.Id, enqueuedConnectionIds);
    }

    private static async Task<string> SetupAndLoginAsync(HttpClient client)
    {
        await client.PostAsJsonAsync("/api/auth/setup", new { name = "Owner", email = SetupEmail, password = SetupPassword });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    private static HttpRequestMessage AuthedPost(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<Connection> SeedConnectionAsync(SyncApiFactory factory, string pluggyItemId = "item-1")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PluggyItemId = pluggyItemId,
            Status = ConnectionStatus.Updated,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static async Task SeedSyncLogAsync(SyncApiFactory factory, Guid connectionId, DateTime startedAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        db.SyncLogs.Add(new SyncLog
        {
            Id = Guid.NewGuid(),
            ConnectionId = connectionId,
            TriggeredBy = SyncTrigger.Manual,
            StartedAt = startedAt,
            FinishedAt = startedAt,
            Status = SyncStatus.Success,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, following
/// AuthApiFactory/WebhookApiFactory's exact combined pattern: JWT+Totp config for a real
/// setup+login flow (SyncEndpoints requires [Authorize]) plus a fake IBackgroundJobClient in
/// place of the real Hangfire server (never registered under "Testing").
/// </summary>
internal sealed class SyncApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

    public FakeBackgroundJobClient BackgroundJobClient { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = "integration-test-signing-key-at-least-32-bytes-long-for-hmac256",
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "30",
                ["Cors:AllowedOrigin"] = "http://localhost:5173",
                ["Totp:EncryptionKey"] = "IZS7xtbC5Uj4lmJzMPDBA7PWgzb5irgqIKSB9jnZnns=",
                ["Sync:ManualSyncCooldownSeconds"] = "60",
            });
        });

        builder.ConfigureServices(services =>
        {
            var descriptorsToRemove = services
                .Where(d =>
                    d.ServiceType == typeof(SimpleFinanceDbContext)
                    || d.ServiceType == typeof(DbContextOptions<SimpleFinanceDbContext>)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().Contains(typeof(SimpleFinanceDbContext))))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<SimpleFinanceDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<IBackgroundJobClient>();
            services.AddSingleton<IBackgroundJobClient>(BackgroundJobClient);
        });
    }
}
