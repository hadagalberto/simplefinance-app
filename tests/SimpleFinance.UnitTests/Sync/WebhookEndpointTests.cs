using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
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
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Sync;

/// <summary>
/// SYNC-01: POST /api/webhooks/pluggy is a durable-queue receiver -- auth-gated (constant-time
/// secret header compare, no JWT), dedup-by-eventId, and does zero synchronous reconciliation
/// work (verified by asserting the fake IBackgroundJobClient.Create was invoked, never by
/// wall-clock timing). Mirrors AuthApiFactory's WebApplicationFactory-against-InMemory pattern
/// from Phase 1 (01-04-SUMMARY.md), with a fake IBackgroundJobClient swapped in so no real
/// Hangfire server/Postgres storage is needed for these tests (Program.cs already skips
/// AddHangfire/AddHangfireServer entirely under the "Testing" environment).
/// </summary>
public class WebhookEndpointTests
{
    private const string WebhookPath = "/api/webhooks/pluggy";

    [Fact]
    public async Task MissingSecretHeader_Returns401_AndPersistsNoWebhookEvent()
    {
        using var factory = new WebhookApiFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = JsonBody(new { eventId = "evt-no-header", @event = "item/updated", itemId = "item-1" }),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await GetWebhookEventsAsync(factory));
        Assert.Empty(factory.BackgroundJobClient.CreatedJobs);
    }

    [Fact]
    public async Task WrongSecretHeader_Returns401_AndPersistsNoWebhookEvent()
    {
        using var factory = new WebhookApiFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = JsonBody(new { eventId = "evt-wrong-header", @event = "item/updated", itemId = "item-1" }),
        };
        request.Headers.Add(WebhookApiFactory.SecretHeaderName, "not-the-configured-secret");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await GetWebhookEventsAsync(factory));
        Assert.Empty(factory.BackgroundJobClient.CreatedJobs);
    }

    [Fact]
    public async Task ValidFreshEvent_PersistsExactlyOneRow_EnqueuesJob_ReturnsOk_NoSyncReconciliationSynchronously()
    {
        using var factory = new WebhookApiFactory();
        using var client = factory.CreateClient();

        var response = await SendValidWebhookAsync(client, "evt-fresh-1", "item/updated", "item-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await GetWebhookEventsAsync(factory);
        var persisted = Assert.Single(events);
        Assert.Equal("evt-fresh-1", persisted.EventId);
        Assert.Equal("item/updated", persisted.EventType);
        Assert.Equal("item-1", persisted.PluggyItemId);
        Assert.Equal(SimpleFinance.Domain.Enums.WebhookEventStatus.Pending, persisted.Status);

        // No synchronous reconciliation: the only downstream effect of a valid POST is a
        // background job being enqueued -- the endpoint itself never resolves
        // IFinancialDataProvider/ISyncReconciler.
        var enqueued = Assert.Single(factory.BackgroundJobClient.CreatedJobs);
        Assert.Contains("ProcessAsync", enqueued.Method.Name);
    }

    [Fact]
    public async Task ReplayedEventId_ReturnsOk_NoSecondRow_NoSecondEnqueue()
    {
        using var factory = new WebhookApiFactory();
        using var client = factory.CreateClient();

        var first = await SendValidWebhookAsync(client, "evt-replay-1", "item/updated", "item-1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await SendValidWebhookAsync(client, "evt-replay-1", "item/updated", "item-1");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var events = await GetWebhookEventsAsync(factory);
        Assert.Single(events);
        Assert.Single(factory.BackgroundJobClient.CreatedJobs);
    }

    [Fact]
    public async Task MalformedPayload_MissingEventId_ReturnsBadRequest()
    {
        using var factory = new WebhookApiFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = JsonBody(new { @event = "item/updated" }),
        };
        request.Headers.Add(WebhookApiFactory.SecretHeaderName, WebhookApiFactory.SecretHeaderValue);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await GetWebhookEventsAsync(factory));
    }

    private static async Task<HttpResponseMessage> SendValidWebhookAsync(HttpClient client, string eventId, string eventType, string itemId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = JsonBody(new { eventId, @event = eventType, itemId }),
        };
        request.Headers.Add(WebhookApiFactory.SecretHeaderName, WebhookApiFactory.SecretHeaderValue);
        return await client.SendAsync(request);
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static async Task<List<WebhookEvent>> GetWebhookEventsAsync(WebhookApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        return await db.WebhookEvents.ToListAsync();
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, following
/// AuthApiFactory's exact pattern (01-04-SUMMARY.md). Program.cs skips AddHangfire/
/// AddHangfireServer entirely under "Testing" (Hangfire.PostgreSql doesn't work against InMemory),
/// so this factory registers a fake <see cref="IBackgroundJobClient"/> in its place -- the
/// endpoint under test resolves IBackgroundJobClient from DI either way.
/// </summary>
internal sealed class WebhookApiFactory : WebApplicationFactory<Program>
{
    internal const string SecretHeaderName = "X-Webhook-Secret";
    internal const string SecretHeaderValue = "integration-test-webhook-secret";

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
                ["Pluggy:ClientId"] = "test-client-id",
                ["Pluggy:ClientSecret"] = "test-client-secret",
                ["Pluggy:WebhookSecretHeaderName"] = SecretHeaderName,
                ["Pluggy:WebhookSecretValue"] = SecretHeaderValue,
            });
        });

        builder.ConfigureServices(services =>
        {
            // EF Core 10 composes DbContextOptions<T> from every IDbContextOptionsConfiguration<T>
            // registered in the container -- remove every SimpleFinanceDbContext-related
            // descriptor before re-registering it against InMemory (same fix AuthApiFactory
            // documents from 01-04).
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

            // Real Hangfire (AddHangfire/AddHangfireServer) is never registered under "Testing"
            // (Program.cs's guard) -- swap in the fake so WebhookEndpoints can still resolve
            // IBackgroundJobClient from DI.
            services.RemoveAll<IBackgroundJobClient>();
            services.AddSingleton<IBackgroundJobClient>(BackgroundJobClient);
        });
    }
}

/// <summary>
/// Records every job Hangfire's IBackgroundJobClient.Enqueue&lt;T&gt; extension would have
/// created, without needing a real Hangfire server/storage. BackgroundJobClientExtensions.Enqueue
/// calls client.Create(Job.FromExpression(methodCall), new EnqueuedState()) under the hood.
/// </summary>
internal sealed class FakeBackgroundJobClient : IBackgroundJobClient
{
    public List<Job> CreatedJobs { get; } = [];

    public string Create(Job job, IState state)
    {
        CreatedJobs.Add(job);
        return Guid.NewGuid().ToString("N");
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}
