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
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// AI-FND-07/08: GET/PUT /api/ai/settings + GET /api/ai/usage HTTP-layer tests — auth gating,
/// key-present projection (and the T-15-14 invariant that the key VALUE never appears in the
/// response), PUT validation (threshold out of range ⇒ 400), a valid PUT persisting to UserSettings,
/// and the usage aggregate returning 200. Mirrors PreferencesEndpointsTests.cs's WebApplicationFactory
/// + EF InMemory pattern.
/// </summary>
public class AiEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    // Sentinel injected as Ai:DeepSeekApiKey — the response must report presence (true) but never
    // echo this literal value anywhere in its JSON (T-15-14).
    private const string DeepSeekKeySentinel = "sk-deepseek-SECRET-VALUE-must-never-serialize";

    [Fact]
    public async Task GetSettings_NoAuthToken_Returns401()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ai/settings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutSettings_NoAuthToken_Returns401()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/ai/settings", ValidPayload());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUsage_NoAuthToken_Returns401()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ai/usage");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSettings_ReportsKeyPresenceButNeverTheKeyValue()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/ai/settings", accessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();

        // The DeepSeek key IS configured (sentinel) but OpenAI is not.
        var body = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.True(body.GetProperty("deepSeekKeyPresent").GetBoolean());
        Assert.False(body.GetProperty("openAiKeyPresent").GetBoolean());

        // Defaults surfaced through.
        Assert.False(body.GetProperty("aiEnabled").GetBoolean());
        Assert.Equal("deepseek-chat", body.GetProperty("aiChatModel").GetString());

        // T-15-14: the secret value must never appear anywhere in the serialized response.
        Assert.DoesNotContain(DeepSeekKeySentinel, raw);
    }

    [Fact]
    public async Task PutSettings_ThresholdOutOfRange_Returns400_AndDoesNotMutateRow()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        await client.SendAsync(AuthedGet("/api/ai/settings", accessToken)); // materialize defaults

        var payload = ValidPayload();
        payload["aiAutoApplyThreshold"] = 2; // invalid: must be in [0, 1]

        var response = await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, payload));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.UserSettings.FirstAsync(s => s.UserId == userId);
        Assert.Equal(0.85, row.AiAutoApplyThreshold); // unchanged default
    }

    [Fact]
    public async Task PutSettings_NegativeBudget_Returns400()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var payload = ValidPayload();
        payload["aiMonthlyBudgetUsd"] = -1;

        var response = await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, payload));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutSettings_AllValid_PersistsAndReturnsRow()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var payload = ValidPayload();
        payload["aiEnabled"] = true;
        payload["aiCategorizationEnabled"] = true;
        payload["aiAutoApplyThreshold"] = 0.5;
        payload["aiMonthlyBudgetUsd"] = 10.0;
        payload["aiChatModel"] = "deepseek-reasoner";

        var response = await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, payload));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("aiEnabled").GetBoolean());
        Assert.Equal("deepseek-reasoner", body.GetProperty("aiChatModel").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.UserSettings.FirstAsync(s => s.UserId == userId);
        Assert.True(row.AiEnabled);
        Assert.True(row.AiCategorizationEnabled);
        Assert.Equal(0.5, row.AiAutoApplyThreshold);
        Assert.Equal(10.0m, row.AiMonthlyBudgetUsd);
        Assert.Equal("deepseek-reasoner", row.AiChatModel);
    }

    // Sentinel submitted as openAiApiKey via the UI — the stored column must be CIPHERTEXT and the value
    // must never re-serialize in any response.
    private const string OpenAiKeySentinel = "sk-openai-UI-SECRET-must-never-serialize";

    [Fact]
    public async Task PutSettings_SetsKey_StoresCiphertext_AndGetReportsPresentButNeverValue()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var payload = ValidPayload();
        payload["openAiApiKey"] = OpenAiKeySentinel; // OpenAI env is NOT configured in the factory

        var putResponse = await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, payload));
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        // The PUT response reports presence but never echoes the value.
        var putRaw = await putResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(OpenAiKeySentinel, putRaw);
        var putBody = JsonSerializer.Deserialize<JsonElement>(putRaw);
        Assert.True(putBody.GetProperty("openAiKeyPresent").GetBoolean());

        // GET now reports present, still never the value.
        var getResponse = await client.SendAsync(AuthedGet("/api/ai/settings", accessToken));
        var getRaw = await getResponse.Content.ReadAsStringAsync();
        Assert.True(JsonSerializer.Deserialize<JsonElement>(getRaw).GetProperty("openAiKeyPresent").GetBoolean());
        Assert.DoesNotContain(OpenAiKeySentinel, getRaw);

        // The DB column holds CIPHERTEXT (not the plaintext) and decrypts back to the plaintext.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.UserSettings.FirstAsync(s => s.UserId == userId);
        Assert.NotNull(row.OpenAiApiKeyEncrypted);
        Assert.DoesNotContain(OpenAiKeySentinel, row.OpenAiApiKeyEncrypted);
        var protector = scope.ServiceProvider.GetRequiredService<Application.Auth.ITotpSecretProtector>();
        Assert.Equal(OpenAiKeySentinel, protector.Decrypt(row.OpenAiApiKeyEncrypted!));
    }

    [Fact]
    public async Task PutSettings_TrimsKeyBeforeStoring()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var payload = ValidPayload();
        payload["openAiApiKey"] = "  " + OpenAiKeySentinel + "\n"; // whitespace-polluted paste

        await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, payload));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.UserSettings.FirstAsync(s => s.UserId == userId);
        var protector = scope.ServiceProvider.GetRequiredService<Application.Auth.ITotpSecretProtector>();
        Assert.Equal(OpenAiKeySentinel, protector.Decrypt(row.OpenAiApiKeyEncrypted!)); // stored trimmed
    }

    [Fact]
    public void Di_ResolvesRoutingLlmClient_AndAllThreeConcreteChatClients()
    {
        using var factory = new AiApiFactory();
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        Assert.IsType<Infrastructure.Ai.Routing.RoutingLlmClient>(sp.GetRequiredService<Application.Ai.ILlmClient>());
        Assert.NotNull(sp.GetRequiredService<Infrastructure.Ai.DeepSeek.DeepSeekLlmClient>());
        Assert.NotNull(sp.GetRequiredService<Infrastructure.Ai.Anthropic.AnthropicLlmClient>());
        Assert.NotNull(sp.GetRequiredService<Infrastructure.Ai.OpenRouter.OpenRouterLlmClient>());
    }

    private const string AnthropicKeySentinel = "sk-ant-UI-SECRET-must-never-serialize";
    private const string OpenRouterKeySentinel = "sk-or-UI-SECRET-must-never-serialize";

    [Fact]
    public async Task PutSettings_AnthropicAndOpenRouterKeys_StoredEncrypted_ReportedPresent_ClearableAndClaudeModelAccepted()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var payload = ValidPayload();
        payload["aiChatModel"] = "claude-sonnet-5-5";
        payload["aiSummaryModel"] = "openai/gpt-5";
        payload["anthropicApiKey"] = AnthropicKeySentinel;
        payload["openRouterApiKey"] = OpenRouterKeySentinel;

        var putResponse = await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, payload));
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var putRaw = await putResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AnthropicKeySentinel, putRaw);
        Assert.DoesNotContain(OpenRouterKeySentinel, putRaw);
        var putBody = JsonSerializer.Deserialize<JsonElement>(putRaw);
        Assert.True(putBody.GetProperty("anthropicKeyPresent").GetBoolean());
        Assert.True(putBody.GetProperty("openRouterKeyPresent").GetBoolean());
        Assert.Equal("claude-sonnet-5-5", putBody.GetProperty("aiChatModel").GetString());
        Assert.Equal("openai/gpt-5", putBody.GetProperty("aiSummaryModel").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            var row = await db.UserSettings.FirstAsync(s => s.UserId == userId);
            var protector = scope.ServiceProvider.GetRequiredService<Application.Auth.ITotpSecretProtector>();
            Assert.DoesNotContain(AnthropicKeySentinel, row.AnthropicApiKeyEncrypted);
            Assert.Equal(AnthropicKeySentinel, protector.Decrypt(row.AnthropicApiKeyEncrypted!));
            Assert.Equal(OpenRouterKeySentinel, protector.Decrypt(row.OpenRouterApiKeyEncrypted!));
        }

        // Explicit "" clears the Anthropic key only; the OpenRouter key (field omitted) stays.
        var clearPayload = ValidPayload();
        clearPayload["anthropicApiKey"] = "";
        var clearRaw = await (await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, clearPayload))).Content.ReadAsStringAsync();
        var clearBody = JsonSerializer.Deserialize<JsonElement>(clearRaw);
        Assert.False(clearBody.GetProperty("anthropicKeyPresent").GetBoolean());
        Assert.True(clearBody.GetProperty("openRouterKeyPresent").GetBoolean());
    }

    [Fact]
    public async Task PutSettings_OmittedKey_LeavesStoredKeyUnchanged()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        // First set the key.
        var setPayload = ValidPayload();
        setPayload["openAiApiKey"] = OpenAiKeySentinel;
        await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, setPayload));

        // Then PUT again WITHOUT the key field (ValidPayload omits it) — the stored key must remain.
        await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, ValidPayload()));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.UserSettings.FirstAsync(s => s.UserId == userId);
        Assert.NotNull(row.OpenAiApiKeyEncrypted);
    }

    [Fact]
    public async Task PutSettings_EmptyStringKey_ClearsStoredKey_RevertingToEnvPresence()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        // Set the OpenAI key (env not configured), then clear it with an explicit empty string.
        var setPayload = ValidPayload();
        setPayload["openAiApiKey"] = OpenAiKeySentinel;
        await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, setPayload));

        var clearPayload = ValidPayload();
        clearPayload["openAiApiKey"] = ""; // explicit clear
        var clearResponse = await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, clearPayload));
        var clearBody = await clearResponse.Content.ReadFromJsonAsync<JsonElement>();

        // OpenAI env is absent, so clearing reverts presence to false.
        Assert.False(clearBody.GetProperty("openAiKeyPresent").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var row = await db.UserSettings.FirstAsync(s => s.UserId == userId);
        Assert.Null(row.OpenAiApiKeyEncrypted);
    }

    [Fact]
    public async Task PutSettings_ClearDeepSeekKey_RevertsToEnvFallback_StillPresent()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        // DeepSeek env IS configured (sentinel), so even after clearing the DB key the env keeps it present.
        var clearPayload = ValidPayload();
        clearPayload["deepSeekApiKey"] = "";
        var response = await client.SendAsync(AuthedPut("/api/ai/settings", accessToken, clearPayload));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("deepSeekKeyPresent").GetBoolean()); // env fallback keeps it present
    }

    [Fact]
    public async Task GetUsage_ReturnsMonthAggregateWithCap()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/ai/usage?month=2026-07", accessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("2026-07", body.GetProperty("month").GetString());
        Assert.Equal(3.00m, body.GetProperty("monthlyBudgetUsd").GetDecimal()); // default cap
        Assert.Equal(0m, body.GetProperty("totalCostUsd").GetDecimal()); // no usage yet
        Assert.Equal(JsonValueKind.Array, body.GetProperty("features").ValueKind);
    }

    [Fact]
    public async Task GetUsage_MalformedMonth_Returns400()
    {
        using var factory = new AiApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedGet("/api/ai/usage?month=not-a-month", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Dictionary<string, object> ValidPayload() => new()
    {
        ["aiEnabled"] = false,
        ["aiCategorizationEnabled"] = false,
        ["aiAssistantEnabled"] = false,
        ["aiInsightsEnabled"] = false,
        ["aiAnomalyEnabled"] = false,
        ["aiForecastEnabled"] = false,
        ["aiMonthlySummaryEnabled"] = false,
        ["aiAutoApplyThreshold"] = 0.85,
        ["aiMonthlyBudgetUsd"] = 3.0,
        ["aiChatModel"] = "deepseek-chat",
        ["aiSummaryModel"] = "deepseek-reasoner",
        ["aiLanguage"] = "pt-BR",
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

    /// <summary>
    /// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, mirroring
    /// PreferencesApiFactory. Injects a DeepSeek API key sentinel so the key-present projection has a
    /// present key to report (while proving the value never serializes).
    /// </summary>
    internal sealed class AiApiFactory : WebApplicationFactory<Program>
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
                    // DeepSeek key present, OpenAI key absent — exercises both branches of the
                    // key-present projection.
                    ["Ai:DeepSeekApiKey"] = DeepSeekKeySentinel,
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

                // AiUsageQueries runs raw Dapper against a real Postgres connection (the InMemory
                // provider can't back it), so swap in a fake at the HTTP boundary — mirrors how the
                // Dashboard tests keep the real-Postgres-only Dapper path out of InMemory factories.
                // The real SQL aggregate is a DashboardQueries-style concern, exercised against
                // Postgres separately; here we only assert the endpoint's shape/auth/cap wiring.
                var queriesDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(AiUsageQueries));
                if (queriesDescriptor is not null)
                {
                    services.Remove(queriesDescriptor);
                }

                services.AddSingleton<AiUsageQueries, FakeAiUsageQueries>();
            });
        }
    }

    /// <summary>Returns no usage rows so the usage endpoint's shape/cap can be asserted without Postgres.</summary>
    private sealed class FakeAiUsageQueries() : AiUsageQueries(null!)
    {
        public override Task<IReadOnlyList<AiFeatureSpend>> GetMonthlySpendByFeatureAsync(
            Guid userId, DateTime monthStartUtc, DateTime nextMonthStartUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AiFeatureSpend>>([]);
    }
}
