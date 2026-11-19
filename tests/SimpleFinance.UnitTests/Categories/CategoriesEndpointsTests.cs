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

namespace SimpleFinance.UnitTests.Categories;

/// <summary>
/// CAT-05: full category CRUD (create/edit/hide-unhide/delete) on top of the existing
/// GET /api/categories group. System categories are editable/hideable but never deletable
/// (403); in-use categories (Transaction/CategoryRule/CategoryMapping references) return 409
/// on delete. Mirrors TransactionsEndpointsTests' minimal WebApplicationFactory pattern.
/// </summary>
public class CategoriesEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task CreateCategory_ValidBodyNoParent_Returns201WithNonSystemCategory()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost(
            "/api/categories", accessToken,
            new { name = "Pets", type = "Expense", icon = "dog", color = "#ff0000" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pets", body.GetProperty("name").GetString());
        Assert.Equal("Expense", body.GetProperty("type").GetString());
        Assert.False(body.GetProperty("isHidden").GetBoolean());
    }

    [Fact]
    public async Task CreateCategory_ParentBelongsToAnotherUser_Returns400()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var otherUsersParent = await SeedCategoryAsync(factory, otherUserId, "Foreign Parent", CategoryType.Expense);

        var response = await client.SendAsync(AuthedPost(
            "/api/categories", accessToken,
            new { name = "Child", type = "Expense", parentCategoryId = otherUsersParent.Id }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateCategory_NonExistentParent_Returns400()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost(
            "/api/categories", accessToken,
            new { name = "Child", type = "Expense", parentCategoryId = Guid.NewGuid() }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateCategory_OnSystemCategory_Succeeds()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var systemCategory = await SeedCategoryAsync(factory, userId, "System Cat", CategoryType.Expense, isSystem: true);

        var response = await client.SendAsync(AuthedPut(
            $"/api/categories/{systemCategory.Id}", accessToken,
            new { name = "Renamed System Cat", type = "Income", icon = "star", color = "#00ff00" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed System Cat", body.GetProperty("name").GetString());
        Assert.Equal("Income", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task UpdateCategory_AnotherUsersCategory_Returns404()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var foreignCategory = await SeedCategoryAsync(factory, otherUserId, "Foreign", CategoryType.Expense);

        var response = await client.SendAsync(AuthedPut(
            $"/api/categories/{foreignCategory.Id}", accessToken,
            new { name = "Hijacked", type = "Expense" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateCategory_MissingId_Returns404()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPut(
            $"/api/categories/{Guid.NewGuid()}", accessToken,
            new { name = "Ghost", type = "Expense" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateCategory_EmptyName_Returns400()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost(
            "/api/categories", accessToken,
            new { name = "", type = "Expense" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateCategory_InvalidType_Returns400()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost(
            "/api/categories", accessToken,
            new { name = "Odd", type = "NotARealType" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ToggleHide_OnSystemCategory_Succeeds()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var systemCategory = await SeedCategoryAsync(factory, userId, "System Cat", CategoryType.Expense, isSystem: true);

        var response = await client.SendAsync(AuthedPatch(
            $"/api/categories/{systemCategory.Id}/hide", accessToken, new { isHidden = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isHidden").GetBoolean());
    }

    [Fact]
    public async Task ToggleHide_MissingIsHiddenField_Returns400()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var category = await SeedCategoryAsync(factory, userId, "Some Cat", CategoryType.Expense);

        var response = await client.SendAsync(AuthedPatch(
            $"/api/categories/{category.Id}/hide", accessToken, new { }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCategory_SystemCategory_Returns403()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var systemCategory = await SeedCategoryAsync(factory, userId, "System Cat", CategoryType.Expense, isSystem: true);

        var response = await client.SendAsync(AuthedDelete($"/api/categories/{systemCategory.Id}", accessToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCategory_ReferencedByTransaction_Returns409()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var category = await SeedCategoryAsync(factory, userId, "In Use", CategoryType.Expense);
        var (_, account) = await SeedConnectionAndAccountAsync(factory, userId, "Bank Co");
        await SeedTransactionAsync(factory, userId, account.Id, category.Id);

        var response = await client.SendAsync(AuthedDelete($"/api/categories/{category.Id}", accessToken));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCategory_UnreferencedNonSystem_Succeeds_AndRowIsGone()
    {
        using var factory = new CategoriesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var category = await SeedCategoryAsync(factory, userId, "Deletable", CategoryType.Expense);

        var response = await client.SendAsync(AuthedDelete($"/api/categories/{category.Id}", accessToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        Assert.False(await db.Categories.AnyAsync(c => c.Id == category.Id));
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

    private static HttpRequestMessage AuthedPut(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static HttpRequestMessage AuthedPatch(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path)
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

    private static async Task<Category> SeedCategoryAsync(
        CategoriesApiFactory factory, Guid userId, string name, CategoryType type, bool isSystem = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var category = new Category
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Type = type,
            IsSystem = isSystem,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    private static async Task<(Connection Connection, Account Account)> SeedConnectionAndAccountAsync(
        CategoriesApiFactory factory, Guid userId, string institutionName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PluggyItemId = $"item-{Guid.NewGuid():N}",
            InstitutionName = institutionName,
            Status = ConnectionStatus.Updated,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Connections.Add(connection);

        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConnectionId = connection.Id,
            PluggyAccountId = $"pluggy-account-{Guid.NewGuid():N}",
            Type = AccountType.Bank,
            Subtype = AccountSubtype.CheckingAccount,
            Name = institutionName,
            Balance = 0m,
            CurrencyCode = "BRL",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);

        await db.SaveChangesAsync();
        return (connection, account);
    }

    private static async Task SeedTransactionAsync(
        CategoriesApiFactory factory, Guid userId, Guid accountId, Guid categoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = accountId,
            PluggyTransactionId = $"pluggy-txn-{Guid.NewGuid():N}",
            Date = now,
            Description = "In-use transaction",
            Amount = 10m,
            Type = TransactionType.Debit,
            CurrencyCode = "BRL",
            Status = TransactionStatus.Posted,
            CategoryId = categoryId,
            CategorySource = CategorySource.SystemMapping,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// TransactionsApiFactory/CategorySeedApiFactory's minimal pattern.
/// </summary>
internal sealed class CategoriesApiFactory : WebApplicationFactory<Program>
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
