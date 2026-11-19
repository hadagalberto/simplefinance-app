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

namespace SimpleFinance.UnitTests.Investments;

/// <summary>
/// INV-01/INV-02: GET /api/investments and GET /api/investments/summary HTTP-layer tests --
/// auth/response-shape concerns. Mirrors DashboardEndpointsTests.cs's minimal WebApplicationFactory
/// + EF InMemory pattern for auth/user data, but InvestmentsService (unlike DashboardService)
/// always reaches InvestmentQueries -- even on the empty-state 200 path -- so this factory ALSO
/// points ConnectionStrings:Default at the same real local Postgres (127.0.0.1:5433) used by
/// InvestmentQueriesTests.cs/InvestmentsServiceTests.cs. Requires `docker compose up postgres -d`
/// running locally first.
/// </summary>
public class InvestmentsEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Theory]
    [InlineData("/api/investments")]
    [InlineData("/api/investments/summary")]
    public async Task GetInvestmentsRoute_NoAuthToken_Returns401(string path)
    {
        using var factory = new InvestmentsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetInvestments_ValidToken_Returns200_EmptyItemsShape()
    {
        using var factory = new InvestmentsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/investments", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.GetProperty("items").ValueKind);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task GetInvestmentsSummary_ValidToken_Returns200_EmptySummaryShape()
    {
        using var factory = new InvestmentsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/investments/summary", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("totalInvested").GetDecimal());
        Assert.Equal(0, body.GetProperty("totalCurrentValue").GetDecimal());
        Assert.Equal(0, body.GetProperty("totalGain").GetDecimal());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("totalGainPercent").ValueKind);
        Assert.Empty(body.GetProperty("byType").EnumerateArray());
        Assert.Empty(body.GetProperty("byIssuer").EnumerateArray());
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
/// auth/user data (mirrors DashboardApiFactory's pattern), but also sets ConnectionStrings:Default
/// to a real local Postgres so Reporting's IDbConnectionFactory (real-Postgres-only) can serve
/// InvestmentQueries for the 200-path tests above.
/// </summary>
internal sealed class InvestmentsApiFactory : WebApplicationFactory<Program>
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
