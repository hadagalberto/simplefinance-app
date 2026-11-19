using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Auth;

/// <summary>
/// End-to-end HTTP tests against the real Minimal API pipeline (AuthEndpoints + Program.cs
/// wiring), backed by an isolated EF Core InMemory database per test (see AuthApiFactory).
/// </summary>
public class AuthEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";
    private const string SetupName = "Owner";

    private static Task<HttpResponseMessage> SetupUserAsync(
        HttpClient client,
        string name = SetupName,
        string email = SetupEmail,
        string password = SetupPassword) =>
        client.PostAsJsonAsync("/api/auth/setup", new { name, email, password });

    [Fact]
    public async Task Setup_FreshDatabase_Returns201AndPersistsArgon2idHashedPassword()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();

        var response = await SetupUserAsync(client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var user = await db.Users.SingleAsync();

        Assert.Equal(SetupEmail, user.Email);
        Assert.NotEqual(SetupPassword, user.PasswordHash);
        Assert.StartsWith("$argon2id$", user.PasswordHash);
    }

    [Fact]
    public async Task Setup_SecondCall_Returns409AndDoesNotCreateSecondRow()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();

        var first = await SetupUserAsync(client);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await SetupUserAsync(client, email: "someoneelse@example.com");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Login_CorrectCredentialsNoTwoFactor_ReturnsAccessTokenAndSetsRefreshCookie()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        await SetupUserAsync(client);

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = SetupEmail,
            password = SetupPassword,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("requiresTwoFactor").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies!, c => c.StartsWith("refresh_token=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_CorrectCredentialsTwoFactorEnabled_ReturnsPendingTokenWithoutCookieOrAccessToken()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        await SetupUserAsync(client);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            var user = await db.Users.SingleAsync();
            user.TotpEnabled = true;
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = SetupEmail,
            password = SetupPassword,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("requiresTwoFactor").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("pendingToken").GetString()));
        Assert.False(body.TryGetProperty("accessToken", out _));

        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401WithGenericError()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        await SetupUserAsync(client);

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = SetupEmail,
            password = "totally-wrong-password",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> LoginAndGetAccessTokenAsync(HttpClient client, string password = SetupPassword)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    private static HttpRequestMessage ChangePasswordRequest(string accessToken, string currentPassword, string newPassword)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password")
        {
            Content = JsonContent.Create(new { currentPassword, newPassword }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    [Fact]
    public async Task ChangePassword_CorrectCurrentPassword_UpdatesHashAndInvalidatesOldPassword()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        await SetupUserAsync(client);

        var accessToken = await LoginAndGetAccessTokenAsync(client);
        const string newPassword = "aNewSecurePassword123";

        var changeResponse = await client.SendAsync(ChangePasswordRequest(accessToken, SetupPassword, newPassword));
        Assert.Equal(HttpStatusCode.NoContent, changeResponse.StatusCode);

        var oldLogin = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = newPassword });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Returns400AndDoesNotAlterHash()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        await SetupUserAsync(client);

        var accessToken = await LoginAndGetAccessTokenAsync(client);

        string originalHash;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            originalHash = (await db.Users.SingleAsync()).PasswordHash;
        }

        var response = await client.SendAsync(
            ChangePasswordRequest(accessToken, "totally-wrong-current-password", "aNewSecurePassword123"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            var currentHash = (await db.Users.SingleAsync()).PasswordHash;
            Assert.Equal(originalHash, currentHash);
        }
    }

    [Fact]
    public async Task FullFlow_SetupLoginRefreshLogout_WorksEndToEndAndRejectsStaleRefreshToken()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();

        var setupResponse = await SetupUserAsync(client);
        Assert.Equal(HttpStatusCode.Created, setupResponse.StatusCode);

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.True(loginResponse.Headers.TryGetValues("Set-Cookie", out var loginCookies));
        var staleRefreshToken = ExtractCookieValue(loginCookies!, "refresh_token");

        // HttpClient's cookie container auto-attaches the refresh cookie captured above.
        var refreshResponse = await client.PostAsync("/api/auth/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        var logoutResponse = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        // Replay the stale (already-rotated-away) refresh token directly — must be rejected.
        // Uses a separate client with cookie handling disabled so the manually-set Cookie
        // header isn't rejected/duplicated by an auto-managed CookieContainer.
        using var rawClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var staleRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        staleRequest.Headers.Add("Cookie", $"refresh_token={staleRefreshToken}");
        var staleResponse = await rawClient.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, staleResponse.StatusCode);
    }

    private static string ExtractCookieValue(IEnumerable<string> setCookieHeaders, string cookieName)
    {
        foreach (var header in setCookieHeaders)
        {
            var firstSegment = header.Split(';')[0];
            var parts = firstSegment.Split('=', 2);
            if (parts.Length == 2 && parts[0] == cookieName)
            {
                return parts[1];
            }
        }

        throw new InvalidOperationException($"Cookie '{cookieName}' not found in Set-Cookie headers.");
    }
}
