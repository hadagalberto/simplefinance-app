using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.NetWorth;

/// <summary>
/// INV-03/INV-04/INV-05: GET /api/networth/summary and GET /api/networth/evolution HTTP-layer
/// tests -- auth/response-shape/validation concerns. Mirrors InvestmentsEndpointsTests.cs's
/// minimal WebApplicationFactory + EF InMemory pattern for auth/user data, but NetWorthService
/// (like InvestmentsService) always reaches DashboardQueries -- even on the empty-state 200 path
/// -- so this factory ALSO points ConnectionStrings:Default at the same real local Postgres
/// (127.0.0.1:5433) used by NetWorthServiceTests.cs/DashboardQueriesTests.cs. Requires
/// `docker compose up postgres -d` running locally first.
/// </summary>
public class NetWorthEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Theory]
    [InlineData("/api/networth/summary")]
    [InlineData("/api/networth/evolution")]
    public async Task GetNetWorthRoute_NoAuthToken_Returns401(string path)
    {
        using var factory = new NetWorthApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSummary_ValidToken_Returns200_EmptySummaryShape()
    {
        using var factory = new NetWorthApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/networth/summary", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("accountsBalance").GetDecimal());
        Assert.Equal(0, body.GetProperty("investmentsBalance").GetDecimal());
        Assert.Equal(0, body.GetProperty("creditCardDebt").GetDecimal());
        Assert.Equal(0, body.GetProperty("netWorth").GetDecimal());
    }

    [Fact]
    public async Task GetEvolution_NoQueryParam_DefaultsTo30Days_Returns200EmptyPoints()
    {
        using var factory = new NetWorthApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/networth/evolution", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.GetProperty("points").ValueKind);
        Assert.Empty(body.GetProperty("points").EnumerateArray());
    }

    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(365)]
    public async Task GetEvolution_AllowedDaysValue_Returns200(int days)
    {
        using var factory = new NetWorthApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet($"/api/networth/evolution?days={days}", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(999999)]
    public async Task GetEvolution_DisallowedDaysValue_Returns400(int days)
    {
        using var factory = new NetWorthApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet($"/api/networth/evolution?days={days}", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database for
/// auth/user data (mirrors InvestmentsApiFactory's pattern), but also sets ConnectionStrings:Default
/// to a real local Postgres so Reporting's IDbConnectionFactory (real-Postgres-only) can serve
/// DashboardQueries for the 200-path tests above.
/// </summary>
internal sealed class NetWorthApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

    private static string GetReportingConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

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
                ["ConnectionStrings:Default"] = GetReportingConnectionString(),
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
