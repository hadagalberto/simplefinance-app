using System.IdentityModel.Tokens.Jwt;
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

namespace SimpleFinance.UnitTests.Backup;

/// <summary>
/// CFG-03/T-08-13/T-08-14: GET /api/backup/export HTTP-layer tests -- correct user-scoping
/// across multiple tables (two-user fixture, zero foreign rows), auth secrets never present, and
/// the "export" rate-limit policy actually rejecting a 6th rapid call. Mirrors
/// BudgetsEndpointsTests.cs's minimal WebApplicationFactory + EF InMemory pattern -- BackupService
/// is pure EF Core (no Dapper/real Postgres needed).
/// </summary>
public class BackupEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetBackupExport_NoAuthToken_Returns401()
    {
        using var factory = new BackupApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/backup/export?format=json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetBackupExport_UnknownFormat_Returns400()
    {
        using var factory = new BackupApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/backup/export?format=csv", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetBackupExport_Json_ReturnsOnlyCallersOwnRows_AcrossMultipleTables()
    {
        using var factory = new BackupApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);
        var userId = DecodeUserIdFromToken(accessToken);

        await SeedFixtureAsync(factory, userId, name: "Mine");

        var otherUserId = Guid.NewGuid();
        await SeedFixtureAsync(factory, otherUserId, name: "TheirsNotMine");

        var response = await client.SendAsync(AuthedGet("/api/backup/export?format=json", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        Assert.StartsWith("simplefinance-backup-", fileName?.Trim('"'));
        Assert.EndsWith(".json", fileName?.Trim('"'));

        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("TheirsNotMine", json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("exportedAt", out _));

        Assert.Equal(1, root.GetProperty("accounts").GetArrayLength());
        Assert.Equal("Mine", root.GetProperty("accounts")[0].GetProperty("name").GetString());

        Assert.Equal(1, root.GetProperty("transactions").GetArrayLength());
        Assert.Equal("Mine", root.GetProperty("transactions")[0].GetProperty("description").GetString());

        // Categories aren't asserted by exact count -- CategorySeeder auto-seeds a full default
        // tree on /api/auth/setup, so "Mine" is one row among many; the "no foreign rows" and
        // "auth secrets absent" contracts are already proven by the DoesNotContain assertion
        // above and the dedicated NeverContainsAuthSecretFields test below.
        Assert.Contains(
            root.GetProperty("categories").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "Mine");
        Assert.Equal(1, root.GetProperty("goals").GetArrayLength());
        Assert.Equal(1, root.GetProperty("alertRules").GetArrayLength());
    }

    [Fact]
    public async Task GetBackupExport_Json_NeverContainsAuthSecretFields()
    {
        using var factory = new BackupApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/backup/export?format=json", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("totpSecret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recoveryCode", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBackupExport_SixthRapidCall_Returns429()
    {
        using var factory = new BackupApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        HttpResponseMessage? lastResponse = null;
        for (var i = 0; i < 6; i++)
        {
            lastResponse = await client.SendAsync(AuthedGet("/api/backup/export?format=json", accessToken));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse!.StatusCode);
    }

    private static async Task SeedFixtureAsync(BackupApiFactory factory, Guid userId, string name)
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
            Name = name,
            Balance = 1000m,
            CurrencyCode = "BRL",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);

        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = account.Id,
            PluggyTransactionId = $"txn-{Guid.NewGuid():N}",
            Date = now,
            Description = name,
            Amount = 50m,
            Type = TransactionType.Debit,
            CurrencyCode = "BRL",
            Status = TransactionStatus.Posted,
            CategorySource = CategorySource.SystemMapping,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.Categories.Add(new Category
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Type = CategoryType.Expense,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.Goals.Add(new Goal
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            TargetAmount = 1000m,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.AlertRules.Add(new AlertRule
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = AlertRuleType.LowBalance,
            Threshold = 100m,
            Channel = NotificationChannelType.Telegram,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync();
    }

    // MapInboundClaims=false (Program.cs) means the JWT's own "sub" claim is readable as-is --
    // decoding it directly here avoids needing a dedicated GET /api/auth/me route just for tests.
    private static Guid DecodeUserIdFromToken(string accessToken)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(accessToken);
        var sub = token.Claims.First(c => c.Type == "sub").Value;
        return Guid.Parse(sub);
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

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// BudgetsApiFactory's minimal pattern. BackupService is pure EF Core -- no real Postgres needed.
/// </summary>
internal sealed class BackupApiFactory : WebApplicationFactory<Program>
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
