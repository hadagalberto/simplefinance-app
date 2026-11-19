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

namespace SimpleFinance.UnitTests.Budgets;

/// <summary>
/// BUD-01..04: GET/POST/DELETE /api/budgets HTTP-layer tests -- covers ONLY auth/validation
/// concerns that don't need real spend data (BudgetService's own copy-forward/spend/state logic
/// is covered end-to-end against real Postgres by BudgetServiceTests). Mirrors
/// CategoriesEndpointsTests.cs's minimal WebApplicationFactory + EF InMemory pattern.
/// </summary>
public class BudgetsEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetBudgets_NoAuthToken_Returns401()
    {
        using var factory = new BudgetsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/budgets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostBudget_NoAuthToken_Returns401()
    {
        using var factory = new BudgetsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/budgets", new { categoryId = (Guid?)null, year = 2026, month = 7, limitAmount = 100m });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBudget_NoAuthToken_Returns401()
    {
        using var factory = new BudgetsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/api/budgets/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task PostBudget_LimitAmountNotPositive_Returns400(decimal limitAmount)
    {
        using var factory = new BudgetsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost(
            "/api/budgets", accessToken,
            new { categoryId = (Guid?)null, year = 2026, month = 7, limitAmount }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostBudget_CategoryBelongsToAnotherUser_Returns400()
    {
        using var factory = new BudgetsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var foreignCategory = await SeedCategoryAsync(factory, otherUserId, "Foreign");

        var response = await client.SendAsync(AuthedPost(
            "/api/budgets", accessToken,
            new { categoryId = foreignCategory.Id, year = 2026, month = 7, limitAmount = 100m }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBudget_NonExistentId_Returns404()
    {
        using var factory = new BudgetsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedDelete($"/api/budgets/{Guid.NewGuid()}", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBudget_AnotherUsersBudget_Returns404()
    {
        using var factory = new BudgetsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var foreignBudget = await SeedBudgetAsync(factory, otherUserId, categoryId: null, 2026, 7, 500m);

        var response = await client.SendAsync(AuthedDelete($"/api/budgets/{foreignBudget.Id}", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBudget_OwnedBudget_Returns204_AndRowIsGone()
    {
        using var factory = new BudgetsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var budget = await SeedBudgetAsync(factory, userId, categoryId: null, 2026, 7, 500m);

        var response = await client.SendAsync(AuthedDelete($"/api/budgets/{budget.Id}", accessToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        Assert.False(await db.Budgets.AnyAsync(b => b.Id == budget.Id));
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

    private static HttpRequestMessage AuthedPost(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static HttpRequestMessage AuthedDelete(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<Category> SeedCategoryAsync(BudgetsApiFactory factory, Guid userId, string name)
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

    private static async Task<Budget> SeedBudgetAsync(
        BudgetsApiFactory factory, Guid userId, Guid? categoryId, int year, int month, decimal limitAmount)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var budget = new Budget
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CategoryId = categoryId,
            Year = year,
            Month = month,
            LimitAmount = limitAmount,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Budgets.Add(budget);
        await db.SaveChangesAsync();
        return budget;
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// CategoriesApiFactory/TransactionsApiFactory's minimal pattern. Reporting's IDbConnectionFactory
/// (real-Postgres-only) is never exercised by these tests -- every scenario here either fails
/// validation/auth/ownership before BudgetService reaches DashboardQueries, or (DELETE) never
/// calls it at all.
/// </summary>
internal sealed class BudgetsApiFactory : WebApplicationFactory<Program>
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
