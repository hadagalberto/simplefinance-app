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

namespace SimpleFinance.UnitTests.Goals;

/// <summary>
/// GOAL-01/02/03: GET/POST/PATCH /api/goals HTTP-layer tests -- GoalService (like
/// RecurringService/BudgetService's spend-free paths) is pure EF Core (no Dapper), so this mirrors
/// RecurringEndpointsTests.cs's minimal WebApplicationFactory + EF InMemory pattern.
/// </summary>
public class GoalsEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetGoals_NoAuthToken_Returns401()
    {
        using var factory = new GoalsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/goals");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostGoal_NoAuthToken_Returns401()
    {
        using var factory = new GoalsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/goals", ValidCreatePayload());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PatchGoal_NoAuthToken_Returns401()
    {
        using var factory = new GoalsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/goals/{Guid.NewGuid()}")
        {
            Content = JsonContent(ValidCreatePayload()),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostGoal_TargetAmountNotPositive_Returns400()
    {
        using var factory = new GoalsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost("/api/goals", accessToken, new
        {
            name = "Bad Goal",
            targetAmount = 0m,
            targetDate = (DateTime?)null,
            linkedAccountId = (Guid?)null,
            linkedInvestmentId = (Guid?)null,
            icon = (string?)null,
            color = (string?)null,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostGoal_BothLinkedAccountAndInvestment_Returns400()
    {
        using var factory = new GoalsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var account = await SeedAccountAsync(factory, userId, 100m);
        var investment = await SeedInvestmentAsync(factory, userId, 200m);

        var response = await client.SendAsync(AuthedPost("/api/goals", accessToken, new
        {
            name = "Both Linked",
            targetAmount = 1000m,
            targetDate = (DateTime?)null,
            linkedAccountId = account.Id,
            linkedInvestmentId = investment.Id,
            icon = (string?)null,
            color = (string?)null,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostGoal_LinkedAccountBelongsToAnotherUser_Returns400()
    {
        using var factory = new GoalsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var foreignAccount = await SeedAccountAsync(factory, otherUserId, 100m);

        var response = await client.SendAsync(AuthedPost("/api/goals", accessToken, new
        {
            name = "Foreign Link",
            targetAmount = 1000m,
            targetDate = (DateTime?)null,
            linkedAccountId = foreignAccount.Id,
            linkedInvestmentId = (Guid?)null,
            icon = (string?)null,
            color = (string?)null,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostGoal_ValidPayload_Returns201_AndPersists()
    {
        using var factory = new GoalsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost("/api/goals", accessToken, ValidCreatePayload()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Vacation Fund", body.GetProperty("name").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        Assert.Equal(1, await db.Goals.CountAsync());
    }

    [Fact]
    public async Task PatchProgress_LinkedGoal_Returns400()
    {
        using var factory = new GoalsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var account = await SeedAccountAsync(factory, userId, 100m);
        var goal = await SeedGoalAsync(factory, userId, "Linked Goal", 1000m, 0m, linkedAccountId: account.Id);

        var response = await client.SendAsync(AuthedPatch(
            $"/api/goals/{goal.Id}/progress", accessToken, new { currentAmount = 500m }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchProgress_UnlinkedGoal_Returns200_AndUpdatesCurrentAmount()
    {
        using var factory = new GoalsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var goal = await SeedGoalAsync(factory, userId, "Manual Goal", 1000m, 0m);

        var response = await client.SendAsync(AuthedPatch(
            $"/api/goals/{goal.Id}/progress", accessToken, new { currentAmount = 500m }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var reloaded = await db.Goals.SingleAsync(g => g.Id == goal.Id);
        Assert.Equal(500m, reloaded.CurrentAmount);
    }

    [Fact]
    public async Task PatchArchive_MissingOrForeignId_Returns404()
    {
        using var factory = new GoalsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var foreignGoal = await SeedGoalAsync(factory, otherUserId, "Not Yours", 1000m, 0m);

        var missingResponse = await client.SendAsync(AuthedPatch(
            $"/api/goals/{Guid.NewGuid()}/archive", accessToken, new { }));
        var foreignResponse = await client.SendAsync(AuthedPatch(
            $"/api/goals/{foreignGoal.Id}/archive", accessToken, new { }));

        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
    }

    [Fact]
    public async Task PatchArchive_OwnedGoal_Returns200_AndSetsArchived()
    {
        using var factory = new GoalsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var goal = await SeedGoalAsync(factory, userId, "To Archive", 1000m, 0m);

        var response = await client.SendAsync(AuthedPatch($"/api/goals/{goal.Id}/archive", accessToken, new { }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var reloaded = await db.Goals.SingleAsync(g => g.Id == goal.Id);
        Assert.Equal(GoalStatus.Archived, reloaded.Status);
    }

    private static object ValidCreatePayload() => new
    {
        name = "Vacation Fund",
        targetAmount = 5000m,
        targetDate = (DateTime?)null,
        linkedAccountId = (Guid?)null,
        linkedInvestmentId = (Guid?)null,
        icon = "plane",
        color = "#00AAFF",
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

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static async Task<Account> SeedAccountAsync(GoalsApiFactory factory, Guid userId, decimal balance)
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

    private static async Task<Investment> SeedInvestmentAsync(GoalsApiFactory factory, Guid userId, decimal? value)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var investment = new Investment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConnectionId = Guid.NewGuid(),
            PluggyInvestmentId = $"inv-{Guid.NewGuid():N}",
            Name = "Test Investment",
            Type = InvestmentType.FixedIncome,
            Value = value,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Investments.Add(investment);
        await db.SaveChangesAsync();
        return investment;
    }

    private static async Task<Goal> SeedGoalAsync(
        GoalsApiFactory factory, Guid userId, string name, decimal targetAmount, decimal currentAmount,
        Guid? linkedAccountId = null, Guid? linkedInvestmentId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var goal = new Goal
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            TargetAmount = targetAmount,
            CurrentAmount = currentAmount,
            LinkedAccountId = linkedAccountId,
            LinkedInvestmentId = linkedInvestmentId,
            Status = GoalStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Goals.Add(goal);
        await db.SaveChangesAsync();
        return goal;
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// RecurringApiFactory's minimal pattern. GoalService only ever touches SimpleFinanceDbContext.
/// </summary>
internal sealed class GoalsApiFactory : WebApplicationFactory<Program>
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
