using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.UnitTests.Notifications;

namespace SimpleFinance.UnitTests.Alerts;

/// <summary>
/// NOTIF-01: GET /api/notification-channels (real IsConfigured status) and
/// POST /api/notification-channels/{type}/test (real send-or-fail) HTTP-layer tests. The
/// "unconfigured" cases use the default factory (both channels unconfigured -- no
/// Notifications:* env/config set, matching 08-02's default-empty appsettings.json section); the
/// "configured" cases swap in FakeNotificationChannel doubles (SimpleFinance.UnitTests.Notifications,
/// internal -- visible across namespaces within this one test assembly) so no real network I/O
/// ever happens in a unit test.
/// </summary>
public class NotificationChannelsEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetChannels_NoAuthToken_Returns401()
    {
        using var factory = new NotificationChannelsApiFactory(configureFakes: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/notification-channels");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetChannels_ReturnsBothTypes_WithRealIsConfiguredState()
    {
        using var factory = new NotificationChannelsApiFactory(configureFakes: false);
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/notification-channels", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetArrayLength());
        for (var i = 0; i < body.GetArrayLength(); i++)
        {
            Assert.False(body[i].GetProperty("isConfigured").GetBoolean());
        }
    }

    [Fact]
    public async Task PostTest_UnconfiguredChannel_Returns400()
    {
        using var factory = new NotificationChannelsApiFactory(configureFakes: false);
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost("/api/notification-channels/Telegram/test", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostTest_UnparseableType_Returns400()
    {
        using var factory = new NotificationChannelsApiFactory(configureFakes: false);
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost("/api/notification-channels/NotAType/test", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostTest_BothIsNotATestableChannel_Returns400()
    {
        using var factory = new NotificationChannelsApiFactory(configureFakes: false);
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost("/api/notification-channels/Both/test", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostTest_ConfiguredChannel_CallsSendAsync_Returns200()
    {
        using var factory = new NotificationChannelsApiFactory(configureFakes: true, shouldThrow: false);
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost("/api/notification-channels/Telegram/test", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("sent").GetBoolean());
        Assert.Equal(1, factory.Telegram!.SendAsyncCallCount);
    }

    [Fact]
    public async Task PostTest_ConfiguredChannel_SendAsyncThrows_Returns502()
    {
        using var factory = new NotificationChannelsApiFactory(configureFakes: true, shouldThrow: true);
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost("/api/notification-channels/Telegram/test", accessToken));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("sent").GetBoolean());
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

    private static HttpRequestMessage AuthedPost(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// GoalsApiFactory's minimal pattern. When <paramref name="configureFakes"/> is true, the real
/// TelegramChannel/EmailChannel DI registrations are swapped for FakeNotificationChannel doubles
/// (both reporting IsConfigured=true) so "configured channel" test paths never touch real
/// network I/O; <see cref="Telegram"/> exposes the fake for call-count assertions.
/// </summary>
internal sealed class NotificationChannelsApiFactory(bool configureFakes, bool shouldThrow = false)
    : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

    public FakeNotificationChannel? Telegram { get; private set; }

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

            if (configureFakes)
            {
                var channelDescriptors = services
                    .Where(d => d.ServiceType == typeof(INotificationChannel))
                    .ToList();
                foreach (var descriptor in channelDescriptors)
                {
                    services.Remove(descriptor);
                }

                Telegram = new FakeNotificationChannel(
                    SimpleFinance.Domain.Enums.NotificationChannelType.Telegram, isConfigured: true, shouldThrow: shouldThrow);
                var email = new FakeNotificationChannel(
                    SimpleFinance.Domain.Enums.NotificationChannelType.Email, isConfigured: true, shouldThrow: shouldThrow);

                services.AddSingleton<INotificationChannel>(Telegram);
                services.AddSingleton<INotificationChannel>(email);
            }
        });
    }
}
