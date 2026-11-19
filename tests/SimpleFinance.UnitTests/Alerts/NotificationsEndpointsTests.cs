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

namespace SimpleFinance.UnitTests.Alerts;

/// <summary>
/// NOTIF-04: GET /api/notifications (list + unread count) and POST /api/notifications/{id}/read
/// HTTP-layer tests -- NotificationService is pure EF Core (no Dapper), mirroring
/// GoalsEndpointsTests.cs's minimal WebApplicationFactory + EF InMemory pattern.
/// </summary>
public class NotificationsEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetNotifications_NoAuthToken_Returns401()
    {
        using var factory = new NotificationsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/notifications");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetNotifications_ReturnsUnreadCountAndNewestFirst_ScopedToCaller()
    {
        using var factory = new NotificationsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var older = await SeedNotificationAsync(factory, userId, "Older", isRead: false, createdAt: DateTime.UtcNow.AddMinutes(-10));
        var newer = await SeedNotificationAsync(factory, userId, "Newer", isRead: false, createdAt: DateTime.UtcNow.AddMinutes(-1));
        await SeedNotificationAsync(factory, userId, "AlreadyRead", isRead: true, createdAt: DateTime.UtcNow.AddMinutes(-5));

        var otherUserId = Guid.NewGuid();
        await SeedNotificationAsync(factory, otherUserId, "NotYours", isRead: false, createdAt: DateTime.UtcNow);

        var response = await client.SendAsync(AuthedGet("/api/notifications", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("unreadCount").GetInt32());

        var items = body.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal(newer.Id, Guid.Parse(items[0].GetProperty("id").GetString()!));
        Assert.Equal(older.Id, Guid.Parse(items[2].GetProperty("id").GetString()!));
    }

    [Fact]
    public async Task GetNotifications_TakeParam_CapsItemsButNotUnreadCount()
    {
        using var factory = new NotificationsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        for (var i = 0; i < 5; i++)
        {
            await SeedNotificationAsync(factory, userId, $"N{i}", isRead: false, createdAt: DateTime.UtcNow.AddMinutes(-i));
        }

        var response = await client.SendAsync(AuthedGet("/api/notifications?take=2", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, body.GetProperty("unreadCount").GetInt32());
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task PostRead_FlipsIsRead_Returns204_AndIsIdempotent()
    {
        using var factory = new NotificationsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var notification = await SeedNotificationAsync(factory, userId, "Unread", isRead: false, createdAt: DateTime.UtcNow);

        var firstResponse = await client.SendAsync(AuthedPost($"/api/notifications/{notification.Id}/read", accessToken));
        var secondResponse = await client.SendAsync(AuthedPost($"/api/notifications/{notification.Id}/read", accessToken));

        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondResponse.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var reloaded = await db.Notifications.SingleAsync(n => n.Id == notification.Id);
        Assert.True(reloaded.IsRead);
    }

    [Fact]
    public async Task PostRead_MissingOrForeignId_Returns404()
    {
        using var factory = new NotificationsApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var otherUserId = Guid.NewGuid();
        var foreignNotification = await SeedNotificationAsync(factory, otherUserId, "NotYours", isRead: false, createdAt: DateTime.UtcNow);

        var missingResponse = await client.SendAsync(AuthedPost($"/api/notifications/{Guid.NewGuid()}/read", accessToken));
        var foreignResponse = await client.SendAsync(AuthedPost($"/api/notifications/{foreignNotification.Id}/read", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
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

    private static async Task<Notification> SeedNotificationAsync(
        NotificationsApiFactory factory, Guid userId, string title, bool isRead, DateTime createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = AlertRuleType.LowBalance,
            Title = title,
            Message = $"{title} message",
            Channel = NotificationChannelType.Telegram,
            IsRead = isRead,
            SentAt = null,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();
        return notification;
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
/// GoalsApiFactory's minimal pattern. NotificationService only ever touches SimpleFinanceDbContext.
/// </summary>
internal sealed class NotificationsApiFactory : WebApplicationFactory<Program>
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
