using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// AI-INS-04: HTTP-layer tests for the insight/anomaly card-strip endpoints — GET /api/ai/insights and
/// POST /api/ai/insights/{id}/dismiss. Proves: both routes are JWT-auth (401 without a token); the list
/// returns ONLY the caller's rows of the requested status (default New), never a foreign user's rows
/// (T-18-18); dismiss on an owned id → 204 and the row becomes Dismissed (and leaves the New queue);
/// dismiss on a foreign/missing id → 404 with NO mutation (IDOR-safe). Mirrors AiSuggestionEndpointsTests'
/// WebApplicationFactory + EF InMemory pattern.
/// </summary>
public class InsightsEndpointTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetInsights_NoAuthToken_Returns401()
    {
        using var factory = new InsightsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ai/insights");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DismissInsight_NoAuthToken_Returns401()
    {
        using var factory = new InsightsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/ai/insights/{Guid.NewGuid()}/dismiss", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetInsights_ReturnsOnlyCallersNewRows()
    {
        using var factory = new InsightsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var mine = await SeedInsightAsync(factory, userId, AiInsightKind.SpendingChange,
            AiInsightSeverity.Warning, AiInsightStatus.New, "mine:1");
        // A Dismissed own row — must NOT appear in the default (New) view.
        await SeedInsightAsync(factory, userId, AiInsightKind.Anomaly,
            AiInsightSeverity.Critical, AiInsightStatus.Dismissed, "mine:2");
        // A foreign user's New row — must NEVER appear in the caller's list (T-18-18).
        await SeedInsightAsync(factory, Guid.NewGuid(), AiInsightKind.Trend,
            AiInsightSeverity.Info, AiInsightStatus.New, "foreign:1");

        var response = await client.SendAsync(AuthedGet("/api/ai/insights?status=new", accessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.Equal(1, rows.GetArrayLength());

        var row = rows[0];
        Assert.Equal(mine, Guid.Parse(row.GetProperty("id").GetString()!));
        Assert.Equal("SpendingChange", row.GetProperty("kind").GetString());
        Assert.Equal("Warning", row.GetProperty("severity").GetString());
        Assert.Equal("New", row.GetProperty("status").GetString());
        Assert.Equal("Título mine:1", row.GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetInsights_DefaultsToNewWhenStatusOmitted()
    {
        using var factory = new InsightsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        await SeedInsightAsync(factory, userId, AiInsightKind.SpendingChange,
            AiInsightSeverity.Info, AiInsightStatus.New, "new:1");
        await SeedInsightAsync(factory, userId, AiInsightKind.SpendingChange,
            AiInsightSeverity.Info, AiInsightStatus.Seen, "seen:1");

        var response = await client.SendAsync(AuthedGet("/api/ai/insights", accessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("New", rows[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Dismiss_OwnInsight_Returns204_AndMarksDismissed()
    {
        using var factory = new InsightsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var id = await SeedInsightAsync(factory, userId, AiInsightKind.Anomaly,
            AiInsightSeverity.Critical, AiInsightStatus.New, "mine:1");

        var response = await client.SendAsync(AuthedPost($"/api/ai/insights/{id}/dismiss", accessToken));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.AiInsights.FirstAsync(i => i.Id == id);
        Assert.Equal(AiInsightStatus.Dismissed, row.Status);

        // No longer in the New queue.
        var list = await client.SendAsync(AuthedGet("/api/ai/insights", accessToken));
        var rows = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, rows.GetArrayLength());
    }

    [Fact]
    public async Task Dismiss_ForeignInsight_Returns404_NoMutation()
    {
        using var factory = new InsightsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        // Insight owned by a DIFFERENT user.
        var foreignId = await SeedInsightAsync(factory, Guid.NewGuid(), AiInsightKind.Anomaly,
            AiInsightSeverity.Critical, AiInsightStatus.New, "foreign:1");

        var response = await client.SendAsync(AuthedPost($"/api/ai/insights/{foreignId}/dismiss", accessToken));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.AiInsights.FirstAsync(i => i.Id == foreignId);
        Assert.Equal(AiInsightStatus.New, row.Status);
    }

    [Fact]
    public async Task Dismiss_MissingInsight_Returns404()
    {
        using var factory = new InsightsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost($"/api/ai/insights/{Guid.NewGuid()}/dismiss", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- helpers ----

    private static async Task<Guid> SeedInsightAsync(
        InsightsApiFactory factory,
        Guid userId,
        AiInsightKind kind,
        AiInsightSeverity severity,
        AiInsightStatus status,
        string dedupKey)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var insight = new AiInsight
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = kind,
            Severity = severity,
            Title = $"Título {dedupKey}",
            Body = "Você gastou mais que o normal <script>alert(1)</script>.",
            PeriodStart = now.AddDays(-30),
            PeriodEnd = now,
            DataJson = "{\"current\":1200.5,\"previous\":800.0}",
            Status = status,
            DedupKey = dedupKey,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.AiInsights.Add(insight);
        await db.SaveChangesAsync();
        return insight.Id;
    }

    private static async Task<(string AccessToken, Guid UserId)> SetupAndLoginAsync(HttpClient client)
    {
        await client.PostAsJsonAsync("/api/auth/setup", new { name = "Owner", email = SetupEmail, password = SetupPassword });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString()!;
        var userId = Guid.Parse(body.GetProperty("user").GetProperty("id").GetString()!);
        return (accessToken, userId);
    }

    private static HttpRequestMessage AuthedGet(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static HttpRequestMessage AuthedPost(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    /// <summary>
    /// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database (no Hangfire
    /// server needed — these endpoints never enqueue).
    /// </summary>
    internal sealed class InsightsApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString("N");

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
            });
        }
    }
}
