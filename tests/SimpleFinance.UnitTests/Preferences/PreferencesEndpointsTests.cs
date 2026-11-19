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
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Preferences;

/// <summary>
/// CFG-02: GET/PUT /api/preferences HTTP-layer tests -- get-or-create materialization,
/// validated update (timezone/cron/theme), and the "no row mutated on validation failure"
/// contract. Mirrors BudgetsEndpointsTests.cs's minimal WebApplicationFactory + EF InMemory
/// pattern. The live Hangfire reschedule side effect itself is skipped under the "Testing"
/// environment (PreferencesService's own IHostEnvironment guard), so these tests only prove
/// the HTTP-layer contract, not the Hangfire call.
/// </summary>
public class PreferencesEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetPreferences_NoAuthToken_Returns401()
    {
        using var factory = new PreferencesApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/preferences");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutPreferences_NoAuthToken_Returns401()
    {
        using var factory = new PreferencesApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/preferences", ValidPayload());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetPreferences_BrandNewUser_MaterializesDefaultsAndIsIdempotent()
    {
        using var factory = new PreferencesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var firstResponse = await client.SendAsync(AuthedGet("/api/preferences", accessToken));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("America/Sao_Paulo", first.GetProperty("timezone").GetString());
        Assert.Equal("BRL", first.GetProperty("baseCurrency").GetString());
        Assert.Equal("0 6,18 * * *", first.GetProperty("syncCronExpression").GetString());
        Assert.Equal("light", first.GetProperty("defaultTheme").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            Assert.Equal(1, await db.UserSettings.CountAsync(s => s.UserId == userId));
        }

        var secondResponse = await client.SendAsync(AuthedGet("/api/preferences", accessToken));
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("timezone").GetString(), second.GetProperty("timezone").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            Assert.Equal(1, await db.UserSettings.CountAsync(s => s.UserId == userId));
        }
    }

    [Fact]
    public async Task PutPreferences_InvalidTimezone_Returns400_AndDoesNotMutateRow()
    {
        using var factory = new PreferencesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        await client.SendAsync(AuthedGet("/api/preferences", accessToken)); // materialize defaults

        var response = await client.SendAsync(AuthedPut("/api/preferences", accessToken, new
        {
            timezone = "Not/A_Zone",
            baseCurrency = "BRL",
            syncCronExpression = "0 6,18 * * *",
            defaultTheme = "light",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.UserSettings.FirstAsync(s => s.UserId == userId);
        Assert.Equal("America/Sao_Paulo", row.Timezone);
    }

    [Fact]
    public async Task PutPreferences_InvalidCron_Returns400_AndDoesNotMutateRow()
    {
        using var factory = new PreferencesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        await client.SendAsync(AuthedGet("/api/preferences", accessToken));

        var response = await client.SendAsync(AuthedPut("/api/preferences", accessToken, new
        {
            timezone = "America/Sao_Paulo",
            baseCurrency = "BRL",
            syncCronExpression = "not a cron",
            defaultTheme = "light",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.UserSettings.FirstAsync(s => s.UserId == userId);
        Assert.Equal("0 6,18 * * *", row.SyncCronExpression);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("dark-mode")]
    [InlineData("")]
    public async Task PutPreferences_InvalidTheme_Returns400(string invalidTheme)
    {
        using var factory = new PreferencesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        await client.SendAsync(AuthedGet("/api/preferences", accessToken));

        var response = await client.SendAsync(AuthedPut("/api/preferences", accessToken, new
        {
            timezone = "America/Sao_Paulo",
            baseCurrency = "BRL",
            syncCronExpression = "0 6,18 * * *",
            defaultTheme = invalidTheme,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutPreferences_AllValid_UpdatesAndReturnsRow()
    {
        using var factory = new PreferencesApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPut("/api/preferences", accessToken, new
        {
            timezone = "America/New_York",
            baseCurrency = "USD",
            syncCronExpression = "0 12 * * *",
            defaultTheme = "dark",
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("America/New_York", body.GetProperty("timezone").GetString());
        Assert.Equal("USD", body.GetProperty("baseCurrency").GetString());
        Assert.Equal("0 12 * * *", body.GetProperty("syncCronExpression").GetString());
        Assert.Equal("dark", body.GetProperty("defaultTheme").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.UserSettings.FirstAsync(s => s.UserId == userId);
        Assert.Equal("America/New_York", row.Timezone);
        Assert.Equal("dark", row.DefaultTheme);
    }

    private static object ValidPayload() => new
    {
        timezone = "America/Sao_Paulo",
        baseCurrency = "BRL",
        syncCronExpression = "0 6,18 * * *",
        defaultTheme = "light",
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

    private static HttpRequestMessage AuthedPut(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// BudgetsApiFactory's minimal pattern.
/// </summary>
internal sealed class PreferencesApiFactory : WebApplicationFactory<Program>
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
