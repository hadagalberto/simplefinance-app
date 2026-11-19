using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Infrastructure.Categorization;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Categories;

/// <summary>
/// CAT-01: verifies CategorySeeder is wired into the setup flow (AuthEndpoints.SetupAsync calls
/// it synchronously right after the User row is saved) and that re-invoking it never duplicates
/// rows -- the same guarantee the startup backstop (CategorySeedStartupTask) relies on.
/// </summary>
public class CategorySeedTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task Setup_FreshDatabase_SeedsCategoryTreeInSameRequest()
    {
        using var factory = new CategorySeedApiFactory();
        using var client = factory.CreateClient();

        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/categories", accessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(rows.GetArrayLength() > 0);

        var topLevel = rows.EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == "Alimentação");
        Assert.Equal(JsonValueKind.Null, topLevel.GetProperty("parentCategoryId").ValueKind);
        Assert.Equal("Expense", topLevel.GetProperty("type").GetString());

        var child = rows.EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == "Mercado");
        Assert.Equal(topLevel.GetProperty("id").GetString(), child.GetProperty("parentCategoryId").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        Assert.True(await db.CategoryMappings.AnyAsync());
    }

    [Fact]
    public async Task SeedForUserAsync_CalledTwice_NeverDuplicatesRows()
    {
        using var factory = new CategorySeedApiFactory();
        using var client = factory.CreateClient();

        await SetupAndLoginAsync(client);

        Guid userId;
        int categoryCountAfterSetup;
        int mappingCountAfterSetup;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            userId = await db.Users.Select(u => u.Id).SingleAsync();
            categoryCountAfterSetup = await db.Categories.CountAsync();
            mappingCountAfterSetup = await db.CategoryMappings.CountAsync();
        }

        Assert.True(categoryCountAfterSetup > 0);
        Assert.True(mappingCountAfterSetup > 0);

        // Simulates the startup backstop re-encountering an already-seeded user.
        using (var scope = factory.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<CategorySeeder>();
            await seeder.SeedForUserAsync(userId, CancellationToken.None);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            Assert.Equal(categoryCountAfterSetup, await db.Categories.CountAsync());
            Assert.Equal(mappingCountAfterSetup, await db.CategoryMappings.CountAsync());
        }
    }

    private static async Task<string> SetupAndLoginAsync(HttpClient client)
    {
        await client.PostAsJsonAsync("/api/auth/setup", new { name = "Owner", email = SetupEmail, password = SetupPassword });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    private static HttpRequestMessage AuthedGet(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}

internal sealed class CategorySeedApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

    public CategorySeedApiFactory()
    {
        ClientOptions.BaseAddress = new Uri("https://localhost");
    }

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
