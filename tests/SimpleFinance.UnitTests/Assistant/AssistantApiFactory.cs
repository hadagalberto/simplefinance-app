using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Application.Ai;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Assistant;

/// <summary>
/// 17-05: boots the real SimpleFinance.Api host against an isolated EF Core InMemory database and swaps
/// the real DeepSeek <see cref="ILlmClient"/> for a scriptable <see cref="FakeLlmClient"/> so the chat
/// SSE endpoint + conversation CRUD can be exercised end-to-end with no API key and no network. Mirrors
/// AiEndpointsTests.AiApiFactory's WebApplicationFactory + InMemory pattern. Each test creates its own
/// factory instance (never shared) so the single-user setup-lock and per-test <see cref="Llm"/> script
/// stay isolated.
///
/// The read tools resolve normally; their Dapper reporting bridges point at the (unconfigured under
/// "Testing") Postgres connection, so a real tool call fails and the dispatcher returns
/// <c>{"error":"tool_failed"}</c> — the streaming contract (tool/token/done frames) is unaffected because
/// the <c>tool</c> chip is emitted before dispatch and the final answer streams from the fake.
/// </summary>
internal sealed class AssistantApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

    /// <summary>The scriptable LLM double — configure it (EnqueueChat / ThrowBudgetOnChat / …) before the request.</summary>
    public FakeLlmClient Llm { get; } = new();

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
                        && d.ServiceType.GetGenericArguments().Contains(typeof(SimpleFinanceDbContext)))
                    // Swap the real DeepSeek-backed ILlmClient (registered via AddHttpClient) for the fake.
                    || d.ServiceType == typeof(ILlmClient))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<SimpleFinanceDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            services.AddSingleton<ILlmClient>(Llm);
        });
    }

    /// <summary>
    /// Runs the single-user setup + login and returns the owner's access token and id. A second
    /// (foreign) user cannot be created through the API (setup is single-user/locked), so IDOR tests
    /// seed foreign-owned rows with a random UserId instead.
    /// </summary>
    public static async Task<(string AccessToken, Guid UserId)> SetupAndLoginAsync(HttpClient client)
    {
        await client.PostAsJsonAsync("/api/auth/setup", new { name = "Owner", email = "owner@example.com", password = "correcthorsebatterystaple" });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = "owner@example.com", password = "correcthorsebatterystaple" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString()!;
        var userId = Guid.Parse(body.GetProperty("user").GetProperty("id").GetString()!);
        return (accessToken, userId);
    }
}
