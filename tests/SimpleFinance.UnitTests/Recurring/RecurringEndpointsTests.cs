using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Recurring;

/// <summary>
/// RECUR-02/03/04: GET /api/recurring, PATCH /api/recurring/{id}/subscription, and PATCH
/// /api/recurring/{id}/active HTTP-layer tests -- RecurringService (like BudgetService's spend
/// path) is pure EF Core (no Dapper), so this mirrors BudgetsEndpointsTests.cs's minimal
/// WebApplicationFactory + EF InMemory pattern, not NetWorthEndpointsTests.cs's real-Postgres one.
/// </summary>
public class RecurringEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetRecurring_NoAuthToken_Returns401()
    {
        using var factory = new RecurringApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/recurring");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PatchSubscription_NoAuthToken_Returns401()
    {
        using var factory = new RecurringApiFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/recurring/{Guid.NewGuid()}/subscription")
        {
            Content = JsonContent(new { isSubscription = true }),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PatchActive_NoAuthToken_Returns401()
    {
        using var factory = new RecurringApiFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/recurring/{Guid.NewGuid()}/active")
        {
            Content = JsonContent(new { isActive = false }),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetRecurring_ValidToken_NoRows_Returns200_EmptyShape()
    {
        using var factory = new RecurringApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/recurring", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("monthlyCommittedCost").GetDecimal());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("items").ValueKind);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task GetRecurring_ValidToken_WithRows_Returns200_ItemsAndMonthlyCommittedCost()
    {
        using var factory = new RecurringApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        await SeedRecurringAsync(factory, userId, "NETFLIX.COM", 39.90m, RecurringFrequency.Monthly, isActive: true);

        var response = await client.SendAsync(AuthedGet("/api/recurring", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(39.90m, body.GetProperty("monthlyCommittedCost").GetDecimal());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("NETFLIX.COM", items[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task PatchSubscription_MissingId_Returns404()
    {
        using var factory = new RecurringApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPatch(
            $"/api/recurring/{Guid.NewGuid()}/subscription", accessToken, new { isSubscription = true }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchSubscription_AnotherUsersRow_Returns404()
    {
        using var factory = new RecurringApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var foreignRow = await SeedRecurringAsync(
            factory, otherUserId, "OTHER USER SUB", 10m, RecurringFrequency.Monthly, isActive: true);

        var response = await client.SendAsync(AuthedPatch(
            $"/api/recurring/{foreignRow.Id}/subscription", accessToken, new { isSubscription = true }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchSubscription_OwnedRow_Returns200_AndFlagIsSet()
    {
        using var factory = new RecurringApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var row = await SeedRecurringAsync(factory, userId, "GYM CO", 100m, RecurringFrequency.Monthly, isActive: true);

        var response = await client.SendAsync(AuthedPatch(
            $"/api/recurring/{row.Id}/subscription", accessToken, new { isSubscription = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var reloaded = await db.RecurringTransactions.SingleAsync(r => r.Id == row.Id);
        Assert.True(reloaded.IsSubscription);
    }

    [Fact]
    public async Task PatchActive_OwnedRow_Returns200_AndFlagIsSet()
    {
        using var factory = new RecurringApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var row = await SeedRecurringAsync(factory, userId, "GYM CO", 100m, RecurringFrequency.Monthly, isActive: true);

        var response = await client.SendAsync(AuthedPatch(
            $"/api/recurring/{row.Id}/active", accessToken, new { isActive = false }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var reloaded = await db.RecurringTransactions.SingleAsync(r => r.Id == row.Id);
        Assert.False(reloaded.IsActive);
    }

    [Fact]
    public async Task PatchActive_MissingId_Returns404()
    {
        using var factory = new RecurringApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPatch(
            $"/api/recurring/{Guid.NewGuid()}/active", accessToken, new { isActive = false }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

    private static HttpRequestMessage AuthedPatch(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = JsonContent(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static async Task<RecurringTransaction> SeedRecurringAsync(
        RecurringApiFactory factory, Guid userId, string displayName, decimal averageAmount,
        RecurringFrequency frequency, bool isActive)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var row = new RecurringTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            NormalizedMerchant = displayName,
            DisplayName = displayName,
            AverageAmount = averageAmount,
            Frequency = frequency,
            FirstSeenDate = now.AddMonths(-3),
            LastSeenDate = now,
            NextExpectedDate = now.AddMonths(1),
            OccurrenceCount = 3,
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.RecurringTransactions.Add(row);
        await db.SaveChangesAsync();
        return row;
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// BudgetsApiFactory's minimal pattern. Reporting's IDbConnectionFactory (real-Postgres-only) is
/// never exercised by these tests -- RecurringService only ever touches SimpleFinanceDbContext.
/// </summary>
internal sealed class RecurringApiFactory : WebApplicationFactory<Program>
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
