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

namespace SimpleFinance.UnitTests.Alerts;

/// <summary>
/// NOTIF-02/CFG-01: GET/POST/PUT/PATCH-active/DELETE /api/alert-rules HTTP-layer tests --
/// AlertRuleService is pure EF Core (no Dapper), mirroring GoalsEndpointsTests.cs's minimal
/// WebApplicationFactory + EF InMemory pattern.
/// </summary>
public class AlertRulesEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetAlertRules_NoAuthToken_Returns401()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/alert-rules");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostAlertRule_NoAuthToken_Returns401()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/alert-rules", ValidLargeTransactionPayload());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAlertRules_ReturnsOnlyCallersOwnRules_WithCategoryAndAccountNames()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var category = await SeedCategoryAsync(factory, userId, "Groceries");
        var account = await SeedAccountAsync(factory, userId, 100m);
        await SeedAlertRuleAsync(factory, userId, AlertRuleType.BudgetThreshold, categoryId: category.Id);

        var otherUserId = Guid.NewGuid();
        await SeedAlertRuleAsync(factory, otherUserId, AlertRuleType.LowBalance, threshold: 50m);

        var response = await client.SendAsync(AuthedGet("/api/alert-rules", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("Groceries", body[0].GetProperty("categoryName").GetString());
        Assert.Equal("BudgetThreshold", body[0].GetProperty("type").GetString());
        _ = account; // seeded but unused by this rule; asserts the join doesn't error when AccountId is null
    }

    [Fact]
    public async Task PostAlertRule_CategoryBelongsToAnotherUser_Returns400()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var foreignCategory = await SeedCategoryAsync(factory, otherUserId, "Not Yours");

        var response = await client.SendAsync(AuthedPost("/api/alert-rules", accessToken, new
        {
            type = "BudgetThreshold",
            threshold = (decimal?)null,
            categoryId = foreignCategory.Id,
            accountId = (Guid?)null,
            daysBefore = (int?)null,
            channel = "Telegram",
            isActive = true,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostAlertRule_AccountBelongsToAnotherUser_Returns400()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var foreignAccount = await SeedAccountAsync(factory, otherUserId, 100m);

        var response = await client.SendAsync(AuthedPost("/api/alert-rules", accessToken, new
        {
            type = "LowBalance",
            threshold = 100m,
            categoryId = (Guid?)null,
            accountId = foreignAccount.Id,
            daysBefore = (int?)null,
            channel = "Telegram",
            isActive = true,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostAlertRule_LowBalanceWithoutThreshold_Returns400()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost("/api/alert-rules", accessToken, new
        {
            type = "LowBalance",
            threshold = (decimal?)null,
            categoryId = (Guid?)null,
            accountId = (Guid?)null,
            daysBefore = (int?)null,
            channel = "Telegram",
            isActive = true,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostAlertRule_InvoiceDueWithDaysBeforeOutOfRange_Returns400()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost("/api/alert-rules", accessToken, new
        {
            type = "InvoiceDue",
            threshold = (decimal?)null,
            categoryId = (Guid?)null,
            accountId = (Guid?)null,
            daysBefore = 91,
            channel = "Telegram",
            isActive = true,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostAlertRule_ValidPayload_Returns201_AndPersists()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost("/api/alert-rules", accessToken, ValidLargeTransactionPayload()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("LargeTransaction", body.GetProperty("type").GetString());
        Assert.Equal("Telegram", body.GetProperty("channel").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        Assert.Equal(1, await db.AlertRules.CountAsync());
    }

    [Fact]
    public async Task PatchActive_TogglesIsActive_ReturnsUpdatedRow()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var rule = await SeedAlertRuleAsync(factory, userId, AlertRuleType.LowBalance, threshold: 100m);

        var response = await client.SendAsync(AuthedPatch($"/api/alert-rules/{rule.Id}/active", accessToken, new { isActive = false }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var reloaded = await db.AlertRules.SingleAsync(r => r.Id == rule.Id);
        Assert.False(reloaded.IsActive);
    }

    [Fact]
    public async Task PatchActive_MissingOrForeignId_Returns404()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var foreignRule = await SeedAlertRuleAsync(factory, otherUserId, AlertRuleType.LowBalance, threshold: 100m);

        var missingResponse = await client.SendAsync(AuthedPatch($"/api/alert-rules/{Guid.NewGuid()}/active", accessToken, new { isActive = false }));
        var foreignResponse = await client.SendAsync(AuthedPatch($"/api/alert-rules/{foreignRule.Id}/active", accessToken, new { isActive = false }));

        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteAlertRule_RemovesRule()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var rule = await SeedAlertRuleAsync(factory, userId, AlertRuleType.LowBalance, threshold: 100m);

        var response = await client.SendAsync(AuthedDelete($"/api/alert-rules/{rule.Id}", accessToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        Assert.Equal(0, await db.AlertRules.CountAsync());
    }

    [Fact]
    public async Task DeleteAlertRule_MissingOrForeignId_Returns404()
    {
        using var factory = new AlertRulesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var foreignRule = await SeedAlertRuleAsync(factory, otherUserId, AlertRuleType.LowBalance, threshold: 100m);

        var missingResponse = await client.SendAsync(AuthedDelete($"/api/alert-rules/{Guid.NewGuid()}", accessToken));
        var foreignResponse = await client.SendAsync(AuthedDelete($"/api/alert-rules/{foreignRule.Id}", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
    }

    private static object ValidLargeTransactionPayload() => new
    {
        type = "LargeTransaction",
        threshold = 500m,
        categoryId = (Guid?)null,
        accountId = (Guid?)null,
        daysBefore = (int?)null,
        channel = "Telegram",
        isActive = true,
    };

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

    private static HttpRequestMessage AuthedPost(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static HttpRequestMessage AuthedPatch(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static HttpRequestMessage AuthedDelete(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static async Task<Category> SeedCategoryAsync(AlertRulesApiFactory factory, Guid userId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var category = new Category
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Type = CategoryType.Expense,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    private static async Task<Account> SeedAccountAsync(AlertRulesApiFactory factory, Guid userId, decimal balance)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConnectionId = Guid.NewGuid(),
            PluggyAccountId = $"acct-{Guid.NewGuid():N}",
            Type = AccountType.Bank,
            Subtype = AccountSubtype.CheckingAccount,
            Name = "Test Account",
            Balance = balance,
            CurrencyCode = "BRL",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static async Task<AlertRule> SeedAlertRuleAsync(
        AlertRulesApiFactory factory,
        Guid userId,
        AlertRuleType type,
        decimal? threshold = null,
        Guid? categoryId = null,
        Guid? accountId = null,
        int? daysBefore = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var rule = new AlertRule
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            Threshold = threshold,
            CategoryId = categoryId,
            AccountId = accountId,
            DaysBefore = daysBefore,
            Channel = NotificationChannelType.Telegram,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// GoalsApiFactory's minimal pattern. AlertRuleService only ever touches SimpleFinanceDbContext.
/// </summary>
internal sealed class AlertRulesApiFactory : WebApplicationFactory<Program>
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
