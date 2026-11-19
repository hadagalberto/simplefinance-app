using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Auth;

/// <summary>
/// End-to-end HTTP tests for AUTH-03's 2FA surface (/2fa/enable, /2fa/activate, /2fa/verify),
/// against the real Minimal API pipeline backed by an isolated EF Core InMemory database
/// (see AuthApiFactory).
/// </summary>
public class TwoFactorEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";
    private const string SetupName = "Owner";

    private static Task<HttpResponseMessage> SetupUserAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/setup", new { name = SetupName, email = SetupEmail, password = SetupPassword });

    private static async Task<string> LoginAndGetAccessTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    private static HttpRequestMessage AuthedPost(string accessToken, string path, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static string ComputeTotpCode(string base32Secret)
    {
        var keyBytes = Base32Encoding.ToBytes(base32Secret);
        var totp = new Totp(keyBytes, step: 30, totpSize: 6);
        return totp.ComputeTotp(DateTime.UtcNow);
    }

    private static async Task<(string Secret, List<string> RecoveryCodes)> EnableAndActivateTwoFactorAsync(HttpClient client)
    {
        var accessToken = await LoginAndGetAccessTokenAsync(client);

        var enableResponse = await client.SendAsync(AuthedPost(accessToken, "/api/auth/2fa/enable"));
        var enableBody = await enableResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secret = enableBody.GetProperty("secret").GetString()!;
        var code = ComputeTotpCode(secret);

        var activateResponse = await client.SendAsync(AuthedPost(accessToken, "/api/auth/2fa/activate", new { code }));
        var activateBody = await activateResponse.Content.ReadFromJsonAsync<JsonElement>();
        var recoveryCodes = activateBody.GetProperty("recoveryCodes").EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        return (secret, recoveryCodes);
    }

    [Fact]
    public async Task Enable_Authenticated_ReturnsSecretUriAndQrCode_WithoutEnablingTwoFactor()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        await SetupUserAsync(client);
        var accessToken = await LoginAndGetAccessTokenAsync(client);

        var response = await client.SendAsync(AuthedPost(accessToken, "/api/auth/2fa/enable"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var secret = body.GetProperty("secret").GetString();
        Assert.False(string.IsNullOrEmpty(secret));
        Assert.Contains("otpauth://totp/", body.GetProperty("otpauthUri").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("qrCodePngBase64").GetString()));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var user = await db.Users.SingleAsync();
        Assert.False(user.TotpEnabled);
        Assert.False(string.IsNullOrEmpty(user.TotpSecret));
        // Stored value must be the encrypted form, never the plaintext secret returned to the caller.
        Assert.NotEqual(secret, user.TotpSecret);
    }

    [Fact]
    public async Task Activate_CorrectCode_EnablesTwoFactorAndReturns8RecoveryCodes()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        await SetupUserAsync(client);

        var (_, recoveryCodes) = await EnableAndActivateTwoFactorAsync(client);

        Assert.Equal(8, recoveryCodes.Count);
        Assert.Equal(8, recoveryCodes.Distinct().Count());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var user = await db.Users.SingleAsync();
        Assert.True(user.TotpEnabled);
    }

    [Fact]
    public async Task Activate_WrongCode_Returns400AndDoesNotEnableTwoFactor()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        await SetupUserAsync(client);
        var accessToken = await LoginAndGetAccessTokenAsync(client);

        await client.SendAsync(AuthedPost(accessToken, "/api/auth/2fa/enable"));

        var activateResponse = await client.SendAsync(AuthedPost(accessToken, "/api/auth/2fa/activate", new { code = "000000" }));

        Assert.Equal(HttpStatusCode.BadRequest, activateResponse.StatusCode);
        var body = await activateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_totp_code", body.GetProperty("error").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var user = await db.Users.SingleAsync();
        Assert.False(user.TotpEnabled);
    }

    [Fact]
    public async Task FullFlow_EnableActivateLoginVerifyWithTotp_CompletesSessionWithRealTokens()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        await SetupUserAsync(client);
        var (secret, _) = await EnableAndActivateTwoFactorAsync(client);

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(loginBody.GetProperty("requiresTwoFactor").GetBoolean());
        var pendingToken = loginBody.GetProperty("pendingToken").GetString()!;

        var verifyCode = ComputeTotpCode(secret);
        var verifyResponse = await client.PostAsJsonAsync("/api/auth/2fa/verify", new { pendingToken, code = verifyCode });

        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var verifyBody = await verifyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(verifyBody.GetProperty("requiresTwoFactor").GetBoolean());
        Assert.False(string.IsNullOrEmpty(verifyBody.GetProperty("accessToken").GetString()));
        Assert.True(verifyResponse.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies!, c => c.StartsWith("refresh_token=", StringComparison.Ordinal));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var user = await db.Users.SingleAsync();
        Assert.NotNull(user.LastLoginAt);
    }

    [Fact]
    public async Task FullFlow_VerifyWithRecoveryCode_CompletesSessionAndCodeCannotBeReused()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        await SetupUserAsync(client);
        var (_, recoveryCodes) = await EnableAndActivateTwoFactorAsync(client);
        var recoveryCode = recoveryCodes[0];

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var pendingToken = loginBody.GetProperty("pendingToken").GetString()!;

        var verifyResponse = await client.PostAsJsonAsync("/api/auth/2fa/verify", new { pendingToken, code = recoveryCode });
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);

        // Reusing the same recovery code (via a fresh pending token from a second login) must
        // fail with a distinguishable error, not the generic invalid-TOTP-code error.
        var secondLoginResponse = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        var secondLoginBody = await secondLoginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondPendingToken = secondLoginBody.GetProperty("pendingToken").GetString()!;

        var reuseResponse = await client.PostAsJsonAsync("/api/auth/2fa/verify", new { pendingToken = secondPendingToken, code = recoveryCode });
        Assert.Equal(HttpStatusCode.Unauthorized, reuseResponse.StatusCode);
        var reuseBody = await reuseResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("recovery_code_already_used", reuseBody.GetProperty("error").GetString());
    }
}
