using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// AI-CAT-05/06: HTTP-layer tests for the suggestion review queue + on-demand backlog run —
/// GET /api/ai/suggestions, POST .../{id}/accept|reject, /bulk-accept, and POST /api/ai/categorize/run.
/// Proves: every route is JWT-auth (401 without a token, T-16-11); the list returns only the caller's
/// Pending rows; accept applies CategorySource=AiSuggested to the transaction and removes the row from
/// the queue; accepting a foreign suggestion is a 404 with no mutation (T-16-12); reject leaves the
/// transaction "Sem categoria"; bulk-accept with an empty Ids list is a 400 (T-16-13); and the run
/// endpoint enqueues AiCategorizationJob and returns 202. Mirrors AiEndpointsTests's
/// WebApplicationFactory + EF InMemory pattern, with a FakeBackgroundJobClient swapped in to assert the
/// enqueue.
/// </summary>
public class AiSuggestionEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task GetSuggestions_NoAuthToken_Returns401()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ai/suggestions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AcceptSuggestion_NoAuthToken_Returns401()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/ai/suggestions/{Guid.NewGuid()}/accept", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RejectSuggestion_NoAuthToken_Returns401()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/ai/suggestions/{Guid.NewGuid()}/reject", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BulkAccept_NoAuthToken_Returns401()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ai/suggestions/bulk-accept", new { ids = new[] { Guid.NewGuid() } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CategorizeRun_NoAuthToken_Returns401()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/ai/categorize/run", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSuggestions_ReturnsOnlyCallersPendingRows()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var seeded = await SeedPendingSuggestionAsync(factory, userId, "Uber trip", "Uber");
        // A foreign user's pending suggestion — must never appear in the caller's queue.
        await SeedPendingSuggestionAsync(factory, Guid.NewGuid(), "Foreign", "Foreign");

        var response = await client.SendAsync(AuthedGet("/api/ai/suggestions", accessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.Equal(1, rows.GetArrayLength());

        var row = rows[0];
        Assert.Equal(seeded.SuggestionId, Guid.Parse(row.GetProperty("id").GetString()!));
        Assert.Equal(seeded.TransactionId, Guid.Parse(row.GetProperty("transactionId").GetString()!));
        Assert.Equal("Uber trip", row.GetProperty("transactionDescription").GetString());
        Assert.Equal("Uber", row.GetProperty("merchantName").GetString());
        Assert.Equal(seeded.CategoryId, Guid.Parse(row.GetProperty("suggestedCategoryId").GetString()!));
        Assert.Equal("Transporte", row.GetProperty("suggestedCategoryName").GetString());
        Assert.Equal("Pending", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Accept_AppliesAiSuggested_AndRemovesFromQueue()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var seeded = await SeedPendingSuggestionAsync(factory, userId, "Uber trip", "Uber");

        var response = await client.SendAsync(
            AuthedPost($"/api/ai/suggestions/{seeded.SuggestionId}/accept", accessToken));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var txn = await db.Transactions.FirstAsync(t => t.Id == seeded.TransactionId);
        Assert.Equal(seeded.CategoryId, txn.CategoryId);
        Assert.Equal(CategorySource.AiSuggested, txn.CategorySource);
        Assert.Equal(0.6, txn.CategoryConfidence);

        var suggestion = await db.AiSuggestions.FirstAsync(s => s.Id == seeded.SuggestionId);
        Assert.Equal(AiSuggestionStatus.Accepted, suggestion.Status);
        Assert.NotNull(suggestion.DecidedAt);

        // No longer in the pending queue.
        var list = await client.SendAsync(AuthedGet("/api/ai/suggestions", accessToken));
        var rows = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, rows.GetArrayLength());
    }

    [Fact]
    public async Task Accept_ForeignSuggestion_Returns404_NoMutation()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        // Suggestion owned by a DIFFERENT user.
        var foreign = await SeedPendingSuggestionAsync(factory, Guid.NewGuid(), "Foreign", "Foreign");

        var response = await client.SendAsync(
            AuthedPost($"/api/ai/suggestions/{foreign.SuggestionId}/accept", accessToken));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var txn = await db.Transactions.FirstAsync(t => t.Id == foreign.TransactionId);
        Assert.Null(txn.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, txn.CategorySource);

        var suggestion = await db.AiSuggestions.FirstAsync(s => s.Id == foreign.SuggestionId);
        Assert.Equal(AiSuggestionStatus.Pending, suggestion.Status);
        Assert.Null(suggestion.DecidedAt);
    }

    [Fact]
    public async Task Reject_MarksRejected_LeavesTransactionUncategorized()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var seeded = await SeedPendingSuggestionAsync(factory, userId, "Uber trip", "Uber");

        var response = await client.SendAsync(
            AuthedPost($"/api/ai/suggestions/{seeded.SuggestionId}/reject", accessToken));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var txn = await db.Transactions.FirstAsync(t => t.Id == seeded.TransactionId);
        Assert.Null(txn.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, txn.CategorySource);

        var suggestion = await db.AiSuggestions.FirstAsync(s => s.Id == seeded.SuggestionId);
        Assert.Equal(AiSuggestionStatus.Rejected, suggestion.Status);
        Assert.NotNull(suggestion.DecidedAt);
    }

    [Fact]
    public async Task BulkAccept_EmptyIds_Returns400()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedPostJson("/api/ai/suggestions/bulk-accept", accessToken, new { ids = Array.Empty<Guid>() }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BulkAccept_AcceptsOwnedPending_AndSkipsForeign()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, userId) = await SetupAndLoginAsync(client);

        var a = await SeedPendingSuggestionAsync(factory, userId, "Uber trip", "Uber");
        var b = await SeedPendingSuggestionAsync(factory, userId, "iFood order", "iFood");
        var foreign = await SeedPendingSuggestionAsync(factory, Guid.NewGuid(), "Foreign", "Foreign");

        var response = await client.SendAsync(AuthedPostJson(
            "/api/ai/suggestions/bulk-accept",
            accessToken,
            new { ids = new[] { a.SuggestionId, b.SuggestionId, foreign.SuggestionId } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("accepted").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        Assert.Equal(CategorySource.AiSuggested, (await db.Transactions.FirstAsync(t => t.Id == a.TransactionId)).CategorySource);
        Assert.Equal(CategorySource.AiSuggested, (await db.Transactions.FirstAsync(t => t.Id == b.TransactionId)).CategorySource);
        // Foreign transaction untouched.
        Assert.Equal(CategorySource.SystemMapping, (await db.Transactions.FirstAsync(t => t.Id == foreign.TransactionId)).CategorySource);
    }

    [Fact]
    public async Task CategorizeRun_EnqueuesJob_Returns202()
    {
        using var factory = new AiSuggestionApiFactory();
        using var client = factory.CreateClient();
        var (accessToken, _) = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(AuthedPost("/api/ai/categorize/run", accessToken));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("enqueued").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("jobId").GetString()));

        // The fake client recorded an AiCategorizationJob.ExecuteAsync enqueue.
        var job = Assert.Single(factory.BackgroundJobClient.CreatedJobs);
        Assert.Equal(typeof(AiCategorizationJob), job.Type);
        Assert.Equal(nameof(AiCategorizationJob.ExecuteAsync), job.Method.Name);
    }

    // ---- helpers ----

    private sealed record SeededSuggestion(Guid SuggestionId, Guid TransactionId, Guid CategoryId);

    /// <summary>
    /// Seeds one Category ("Transporte") + one uncategorized Transaction + one Pending Category
    /// suggestion (payload {"categoryId":…}, Confidence 0.6) for the given owner, straight into the
    /// shared InMemory database.
    /// </summary>
    private static async Task<SeededSuggestion> SeedPendingSuggestionAsync(
        AiSuggestionApiFactory factory, Guid userId, string description, string merchant)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;

        var category = new Category
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Transporte",
            Type = CategoryType.Expense,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = Guid.NewGuid(),
            PluggyTransactionId = Guid.NewGuid().ToString("N"),
            Date = now,
            Description = description,
            MerchantName = merchant,
            Amount = -25.0m,
            Type = TransactionType.Debit,
            CurrencyCode = "BRL",
            Status = TransactionStatus.Posted,
            CategoryId = null,
            CategorySource = CategorySource.SystemMapping,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var suggestion = new AiSuggestion
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = AiSuggestionType.Category,
            TransactionId = transaction.Id,
            SuggestedPayload = JsonSerializer.Serialize(new { categoryId = category.Id }),
            Confidence = 0.6,
            Rationale = "Similar merchants map to Transporte.",
            Source = AiSuggestionSource.Embedding,
            Status = AiSuggestionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Categories.Add(category);
        db.Transactions.Add(transaction);
        db.AiSuggestions.Add(suggestion);
        await db.SaveChangesAsync();

        return new SeededSuggestion(suggestion.Id, transaction.Id, category.Id);
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

    private static HttpRequestMessage AuthedPostJson(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    /// <summary>
    /// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, swapping in
    /// a FakeBackgroundJobClient so the categorize/run enqueue can be asserted without a real Hangfire
    /// server/storage (unavailable under "Testing").
    /// </summary>
    internal sealed class AiSuggestionApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString("N");

        public FakeBackgroundJobClient BackgroundJobClient { get; } = new();

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

                // Swap the NoOp for a recording fake so the run test can assert the enqueued job.
                services.RemoveAll<IBackgroundJobClient>();
                services.AddSingleton<IBackgroundJobClient>(BackgroundJobClient);
            });
        }
    }

    /// <summary>
    /// Records every job Hangfire's IBackgroundJobClient.Enqueue&lt;T&gt; extension would have created,
    /// without a real Hangfire server/storage. BackgroundJobClientExtensions.Enqueue calls
    /// client.Create(Job.FromExpression(methodCall), new EnqueuedState()) under the hood.
    /// </summary>
    internal sealed class FakeBackgroundJobClient : IBackgroundJobClient
    {
        public List<Job> CreatedJobs { get; } = [];

        public string Create(Job job, IState state)
        {
            CreatedJobs.Add(job);
            return Guid.NewGuid().ToString("N");
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }
}
