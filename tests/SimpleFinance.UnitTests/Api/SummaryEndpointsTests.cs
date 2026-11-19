using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Application.Ai;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Api;

/// <summary>
/// AI-FCT-04: HTTP-layer tests for the monthly-summary read + resend endpoints — GET /api/ai/summary and
/// POST /api/ai/summary/{id}/resend. Proves: both routes are JWT-auth (401 without a token); GET returns
/// ONLY the caller's MonthlySummary for the requested month (default current UTC month), 404 for a foreign
/// or missing month (T-19-12); resend re-dispatches the STORED Title/Body EXACTLY once via the dispatcher
/// with NO new AiInsight and NO LLM call (T-19-13); a foreign/missing id ⇒ 404 with NO dispatch (IDOR-safe).
/// Mirrors InsightsEndpointTests' WebApplicationFactory + EF InMemory pattern, adding a spy notification
/// channel (counts dispatches + captures the strings) and a spy ILlmClient (flags any ChatAsync call).
/// </summary>
public class SummaryEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetSummary_NoAuthToken_Returns401()
    {
        using var factory = new SummaryApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ai/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ResendSummary_NoAuthToken_Returns401()
    {
        using var factory = new SummaryApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/ai/summary/{Guid.NewGuid()}/resend", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSummary_ReturnsCallersSummaryForMonth()
    {
        using var factory = new SummaryApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var monthStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = await SeedSummaryAsync(factory, userId, monthStart, "2026-06");

        var response = await client.SendAsync(AuthedGet("/api/ai/summary?month=2026-06", accessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, Guid.Parse(row.GetProperty("id").GetString()!));
        Assert.Equal("Resumo de junho", row.GetProperty("title").GetString());
        Assert.Equal("Você gastou R$ 1.200 <script>alert(1)</script>.", row.GetProperty("body").GetString());
        Assert.Equal("{\"period\":\"2026-06\",\"totals\":{\"expense\":1200.0}}", row.GetProperty("dataJson").GetString());
    }

    [Fact]
    public async Task GetSummary_ForeignMonth_Returns404()
    {
        using var factory = new SummaryApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        // The caller has a summary for 2026-06, but asks for 2026-05 (no row) ⇒ 404.
        await SeedSummaryAsync(factory, userId, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), "2026-06");
        // A DIFFERENT user has a summary for 2026-05 — must NEVER leak to the caller (T-19-12).
        await SeedSummaryAsync(factory, Guid.NewGuid(), new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), "2026-05");

        var response = await client.SendAsync(AuthedGet("/api/ai/summary?month=2026-05", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResendSummary_OwnSummary_ReDispatchesStoredStringsOnce_NoNewInsight_NoLlmCall()
    {
        using var factory = new SummaryApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var monthStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = await SeedSummaryAsync(factory, userId, monthStart, "2026-06");

        var beforeCount = await CountInsightsAsync(factory);

        var response = await client.SendAsync(AuthedPost($"/api/ai/summary/{id}/resend", accessToken));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Dispatched EXACTLY once, with the STORED Title/Body verbatim.
        Assert.Single(factory.Channel.Sent);
        var (title, message, email) = factory.Channel.Sent[0];
        Assert.Equal("Resumo de junho", title);
        Assert.Equal("Você gastou R$ 1.200 <script>alert(1)</script>.", message);
        Assert.Equal(SetupEmail, email);

        // NO new AiInsight created (resend re-pushes, never regenerates — T-19-13).
        Assert.Equal(beforeCount, await CountInsightsAsync(factory));

        // NO LLM call whatsoever.
        Assert.False(factory.Llm.WasCalled);
    }

    [Fact]
    public async Task ResendSummary_ForeignSummary_Returns404_NoDispatch()
    {
        using var factory = new SummaryApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        // Summary owned by a DIFFERENT user.
        var foreignId = await SeedSummaryAsync(
            factory, Guid.NewGuid(), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), "2026-06");

        var response = await client.SendAsync(AuthedPost($"/api/ai/summary/{foreignId}/resend", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Channel.Sent);
        Assert.False(factory.Llm.WasCalled);
    }

    [Fact]
    public async Task ResendSummary_MissingSummary_Returns404()
    {
        using var factory = new SummaryApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost($"/api/ai/summary/{Guid.NewGuid()}/resend", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Channel.Sent);
    }

    // ---- helpers ----

    private static async Task<int> CountInsightsAsync(SummaryApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        return await db.AiInsights.CountAsync();
    }

    private static async Task<Guid> SeedSummaryAsync(
        SummaryApiFactory factory,
        Guid userId,
        DateTime monthStartUtc,
        string periodLabel)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var insight = new AiInsight
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = AiInsightKind.MonthlySummary,
            Severity = AiInsightSeverity.Info,
            Title = "Resumo de junho",
            Body = "Você gastou R$ 1.200 <script>alert(1)</script>.",
            PeriodStart = monthStartUtc,
            PeriodEnd = monthStartUtc.AddMonths(1).AddDays(-1),
            DataJson = "{\"period\":\"2026-06\",\"totals\":{\"expense\":1200.0}}",
            Status = AiInsightStatus.New,
            DedupKey = $"MonthlySummary:{periodLabel}",
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
    /// Counting notification channel: always configured, records every (title, message, email) dispatch so
    /// a test can assert resend re-pushed the STORED strings EXACTLY once.
    /// </summary>
    internal sealed class SpyChannel : INotificationChannel
    {
        public List<(string Title, string Message, string Email)> Sent { get; } = new();

        public NotificationChannelType Type => NotificationChannelType.Telegram;

        public bool IsConfigured => true;

        public Task SendAsync(string title, string message, string recipientEmail, CancellationToken ct)
        {
            Sent.Add((title, message, recipientEmail));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Spy ILlmClient: flips <see cref="WasCalled"/> on any call so a test can PROVE resend never touched the
    /// reasoner (the resend endpoint has no ILlmClient dependency; this makes the guarantee explicit).
    /// </summary>
    internal sealed class SpyLlmClient : ILlmClient
    {
        public bool WasCalled { get; private set; }

        public Task<LlmResult> ChatAsync(LlmChatRequest request, CancellationToken ct)
        {
            WasCalled = true;
            throw new InvalidOperationException("resend must never call the LLM");
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            LlmChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            WasCalled = true;
            throw new InvalidOperationException("resend must never call the LLM");
#pragma warning disable CS0162 // unreachable — satisfies the iterator contract
            yield break;
#pragma warning restore CS0162
        }
    }

    /// <summary>
    /// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, swapping the
    /// notification channels for a single counting <see cref="SpyChannel"/> and the ILlmClient for a
    /// <see cref="SpyLlmClient"/> so resend behavior (dispatch-once, no-regenerate, no-LLM) is inspectable.
    /// </summary>
    internal sealed class SummaryApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString("N");

        public SpyChannel Channel { get; } = new();

        public SpyLlmClient Llm { get; } = new();

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
                        || d.ServiceType == typeof(INotificationChannel)
                        || d.ServiceType == typeof(ILlmClient)
                        || (d.ServiceType.IsGenericType
                            && d.ServiceType.GetGenericArguments().Contains(typeof(SimpleFinanceDbContext))))
                    .ToList();

                foreach (var descriptor in descriptorsToRemove)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<SimpleFinanceDbContext>(options =>
                    options.UseInMemoryDatabase(_databaseName));

                services.AddSingleton<INotificationChannel>(Channel);
                services.AddSingleton<ILlmClient>(Llm);
            });
        }
    }
}
