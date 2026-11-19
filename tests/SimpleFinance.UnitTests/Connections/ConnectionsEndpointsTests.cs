using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.UnitTests.Sync;

namespace SimpleFinance.UnitTests.Connections;

/// <summary>
/// PLG-02/03/04: mint a fresh Connect Token every call (never cached/reused), register a
/// Connection from a novel itemId (idempotent-by-itemId, 409 on true duplicate), trigger a first
/// sync via the shared ISyncReconciler (SyncTrigger.Manual), and surface a distinct
/// suggestedAction per documented Status/ExecutionStatus combination. Mirrors
/// SyncEndpointsTests/WebhookEndpointTests's WebApplicationFactory-against-InMemory pattern, with
/// a fake IFinancialDataProvider swapped in so no real Pluggy HTTP call is ever attempted.
/// </summary>
public class ConnectionsEndpointsTests
{
    private const string SetupEmail = "owner@example.com";
    private const string SetupPassword = "correcthorsebatterystaple";

    [Fact]
    public async Task ConnectToken_NoAuth_Returns401()
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/pluggy/connect-token", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConnectToken_CalledTwice_ReturnsTwoDifferentTokenValues()
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var first = await client.SendAsync(AuthedJsonPost("/api/pluggy/connect-token", accessToken, new { }));
        var second = await client.SendAsync(AuthedJsonPost("/api/pluggy/connect-token", accessToken, new { }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstToken = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connectToken").GetString();
        var secondToken = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connectToken").GetString();

        Assert.NotNull(firstToken);
        Assert.NotNull(secondToken);
        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal(2, factory.FinancialDataProvider.ConnectTokenRequests.Count);
        Assert.All(factory.FinancialDataProvider.ConnectTokenRequests, itemId => Assert.Null(itemId));
    }

    [Fact]
    public async Task RegisterConnection_NovelItemId_Returns201_PersistsRealStatus_EnqueuesManualSync()
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        factory.FinancialDataProvider.ItemStatusByItemId["item-novel"] =
            new ProviderItemStatus("UPDATING", "ACCOUNTS_IN_PROGRESS", null, null, null, 42, "connector-x", "Bank X", "https://img/x.png");

        var response = await client.SendAsync(
            AuthedJsonPost("/api/connections", accessToken, new { pluggyItemId = "item-novel" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updating", body.GetProperty("status").GetString());
        Assert.Equal("Bank X", body.GetProperty("institutionName").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var connection = await db.Connections.SingleAsync(c => c.PluggyItemId == "item-novel");
        Assert.Equal(ConnectionStatus.Updating, connection.Status);
        Assert.Equal("Bank X", connection.InstitutionName);

        var enqueued = Assert.Single(factory.BackgroundJobClient.CreatedJobs);
        Assert.Equal("SyncConnectionAsync", enqueued.Method.Name);
        Assert.Equal(connection.Id, enqueued.Args[0]);
        Assert.Equal(SyncTrigger.Manual, enqueued.Args[1]);
    }

    [Fact]
    public async Task RegisterConnection_DuplicateItemId_Returns409_NoSecondRow_NoSecondEnqueue()
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var first = await client.SendAsync(AuthedJsonPost("/api/connections", accessToken, new { pluggyItemId = "item-dup" }));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.SendAsync(AuthedJsonPost("/api/connections", accessToken, new { pluggyItemId = "item-dup" }));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var count = await db.Connections.CountAsync(c => c.PluggyItemId == "item-dup");
        Assert.Equal(1, count);
        Assert.Single(factory.BackgroundJobClient.CreatedJobs);
    }

    [Theory]
    [InlineData("UPDATING", "ACCOUNTS_IN_PROGRESS", "WaitForCompletion")]
    [InlineData("UPDATED", "SUCCESS", "None")]
    [InlineData("UPDATED", "PARTIAL_SUCCESS", "ReviewPartialFailure")]
    [InlineData("WAITING_USER_INPUT", "WAITING_USER_INPUT", "ProvideMfa")]
    [InlineData("LOGIN_ERROR", "INVALID_CREDENTIALS", "ReconnectRequired")]
    [InlineData("OUTDATED", "SOME_UNKNOWN_CODE", "GenericRetry")]
    public async Task ListConnections_SurfacesSuggestedActionMatchingItemStatusMapper(
        string status, string executionStatus, string expectedSuggestedAction)
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var itemId = $"item-{status}-{executionStatus}";
        factory.FinancialDataProvider.ItemStatusByItemId[itemId] =
            new ProviderItemStatus(status, executionStatus, null, null, null, null, null, null, null);

        var registerResponse = await client.SendAsync(
            AuthedJsonPost("/api/connections", accessToken, new { pluggyItemId = itemId }));
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        var listResponse = await client.SendAsync(AuthedGet("/api/connections", accessToken));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var connectionId = (await factory.GetConnectionIdAsync(itemId)).ToString();
        var rows = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var row = rows.EnumerateArray().Single(r => r.GetProperty("id").GetString() == connectionId);

        Assert.Equal(expectedSuggestedAction, row.GetProperty("suggestedAction").GetString());
    }

    [Fact]
    public async Task ListConnections_WaitingUserInput_And_LoginError_ProduceDifferentSuggestedActions()
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        factory.FinancialDataProvider.ItemStatusByItemId["item-mfa"] =
            new ProviderItemStatus("WAITING_USER_INPUT", "WAITING_USER_INPUT", null, null, null, null, null, null, null);
        factory.FinancialDataProvider.ItemStatusByItemId["item-login-error"] =
            new ProviderItemStatus("LOGIN_ERROR", "INVALID_CREDENTIALS", null, null, null, null, null, null, null);

        await client.SendAsync(AuthedJsonPost("/api/connections", accessToken, new { pluggyItemId = "item-mfa" }));
        await client.SendAsync(AuthedJsonPost("/api/connections", accessToken, new { pluggyItemId = "item-login-error" }));

        var listResponse = await client.SendAsync(AuthedGet("/api/connections", accessToken));
        var rows = await listResponse.Content.ReadFromJsonAsync<JsonElement>();

        var mfaConnectionId = (await factory.GetConnectionIdAsync("item-mfa")).ToString();
        var loginErrorConnectionId = (await factory.GetConnectionIdAsync("item-login-error")).ToString();

        var mfaAction = rows.EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == mfaConnectionId)
            .GetProperty("suggestedAction").GetString();
        var loginErrorAction = rows.EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == loginErrorConnectionId)
            .GetProperty("suggestedAction").GetString();

        Assert.Equal("ProvideMfa", mfaAction);
        Assert.Equal("ReconnectRequired", loginErrorAction);
        Assert.NotEqual(mfaAction, loginErrorAction);
    }

    [Fact]
    public async Task ReconnectToken_ExistingConnection_ReturnsFreshTokenScopedToExistingItemId()
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory, "item-reconnect");

        var response = await client.SendAsync(
            AuthedPost($"/api/connections/{connection.Id}/reconnect-token", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("connectToken").GetString()));

        var request = Assert.Single(factory.FinancialDataProvider.ConnectTokenRequests);
        Assert.Equal("item-reconnect", request);
    }

    [Fact]
    public async Task ReconnectToken_MissingConnection_Returns404()
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var response = await client.SendAsync(
            AuthedPost($"/api/connections/{Guid.NewGuid()}/reconnect-token", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.FinancialDataProvider.ConnectTokenRequests);
    }

    [Fact]
    public async Task RemoveConnection_NoBody_Returns400_NoDeletionNoPluggyCall()
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory, "item-no-confirm");

        var response = await client.SendAsync(AuthedDelete($"/api/connections/{connection.Id}", accessToken, body: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.FinancialDataProvider.DeleteItemCalls);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        Assert.True(await db.Connections.AnyAsync(c => c.Id == connection.Id));
    }

    [Fact]
    public async Task RemoveConnection_ConfirmFalse_Returns400_NoDeletionNoPluggyCall()
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory, "item-confirm-false");

        var response = await client.SendAsync(
            AuthedDelete($"/api/connections/{connection.Id}", accessToken, body: new { confirm = false }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.FinancialDataProvider.DeleteItemCalls);
    }

    [Fact]
    public async Task RemoveConnection_ConfirmTrue_CallsPluggyDelete_CascadesSoftDelete_Returns204()
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory, "item-remove-me");
        var (account, creditCardDetail) = await SeedAccountWithCreditCardAsync(factory, connection);
        var transaction = await SeedTransactionAsync(factory, account);
        var investment = await SeedInvestmentAsync(factory, connection);

        var response = await client.SendAsync(
            AuthedDelete($"/api/connections/{connection.Id}", accessToken, body: new { confirm = true }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var pluggyDeleteCall = Assert.Single(factory.FinancialDataProvider.DeleteItemCalls);
        Assert.Equal("item-remove-me", pluggyDeleteCall);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        Assert.False(await db.Connections.AnyAsync(c => c.Id == connection.Id));
        Assert.False(await db.Accounts.AnyAsync(a => a.Id == account.Id));
        Assert.False(await db.Investments.AnyAsync(i => i.Id == investment.Id));
        Assert.False(await db.Transactions.AnyAsync(t => t.Id == transaction.Id));
        // CreditCardDetail has no soft-delete column (AuditableEntity) -- hard-deleted instead.
        Assert.False(await db.CreditCardDetails.AnyAsync(c => c.Id == creditCardDetail.Id));

        Assert.True(await db.Connections.IgnoreQueryFilters().AnyAsync(c => c.Id == connection.Id && c.DeletedAt != null));
        Assert.True(await db.Accounts.IgnoreQueryFilters().AnyAsync(a => a.Id == account.Id && a.DeletedAt != null));
        Assert.True(await db.Investments.IgnoreQueryFilters().AnyAsync(i => i.Id == investment.Id && i.DeletedAt != null));
        Assert.True(await db.Transactions.IgnoreQueryFilters().AnyAsync(t => t.Id == transaction.Id && t.DeletedAt != null));
    }

    [Fact]
    public async Task RemoveConnection_CalledTwice_SecondCallReturns404_NoSecondPluggyDelete()
    {
        using var factory = new ConnectionsApiFactory();
        using var client = factory.CreateClient();
        var accessToken = await SetupAndLoginAsync(client);

        var connection = await SeedConnectionAsync(factory, "item-remove-twice");

        var first = await client.SendAsync(
            AuthedDelete($"/api/connections/{connection.Id}", accessToken, body: new { confirm = true }));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await client.SendAsync(
            AuthedDelete($"/api/connections/{connection.Id}", accessToken, body: new { confirm = true }));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        Assert.Single(factory.FinancialDataProvider.DeleteItemCalls);
    }

    private static async Task<string> SetupAndLoginAsync(HttpClient client)
    {
        await client.PostAsJsonAsync("/api/auth/setup", new { name = "Owner", email = SetupEmail, password = SetupPassword });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = SetupEmail, password = SetupPassword });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    private static HttpRequestMessage AuthedJsonPost(string path, string accessToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
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

    private static HttpRequestMessage AuthedDelete(string path, string accessToken, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<Connection> SeedConnectionAsync(ConnectionsApiFactory factory, string pluggyItemId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PluggyItemId = pluggyItemId,
            Status = ConnectionStatus.Updated,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static async Task<(Account Account, CreditCardDetail CreditCardDetail)> SeedAccountWithCreditCardAsync(
        ConnectionsApiFactory factory, Connection connection)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = connection.UserId,
            ConnectionId = connection.Id,
            PluggyAccountId = $"pluggy-account-{Guid.NewGuid():N}",
            Type = AccountType.Credit,
            Subtype = AccountSubtype.CreditCard,
            Name = "Test Card",
            CurrencyCode = "BRL",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);

        var creditCardDetail = new CreditCardDetail
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Status = CreditCardStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CreditCardDetails.Add(creditCardDetail);

        await db.SaveChangesAsync();
        return (account, creditCardDetail);
    }

    private static async Task<Transaction> SeedTransactionAsync(ConnectionsApiFactory factory, Account account)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            AccountId = account.Id,
            PluggyTransactionId = $"pluggy-txn-{Guid.NewGuid():N}",
            Date = now,
            Description = "Test transaction",
            Amount = 10.00m,
            Type = TransactionType.Debit,
            CurrencyCode = "BRL",
            Status = TransactionStatus.Posted,
            CategorySource = CategorySource.SystemMapping,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        return transaction;
    }

    private static async Task<Investment> SeedInvestmentAsync(ConnectionsApiFactory factory, Connection connection)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();

        var now = DateTime.UtcNow;
        var investment = new Investment
        {
            Id = Guid.NewGuid(),
            UserId = connection.UserId,
            ConnectionId = connection.Id,
            PluggyInvestmentId = $"pluggy-inv-{Guid.NewGuid():N}",
            Name = "Test Investment",
            Type = InvestmentType.FixedIncome,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Investments.Add(investment);
        await db.SaveChangesAsync();
        return investment;
    }
}

/// <summary>
/// Boots the real SimpleFinance.Api host against an isolated EF Core InMemory database, following
/// SyncApiFactory/WebhookApiFactory's exact combined pattern: JWT+Totp config for a real
/// setup+login flow, a fake IBackgroundJobClient (no real Hangfire storage under "Testing"), and
/// -- new for this plan -- a fake IFinancialDataProvider swapped in, since ConnectionsEndpoints
/// calls it synchronously within the HTTP request (connect-token/register/reconnect-token/
/// remove), unlike SyncEndpoints/WebhookEndpoints which only ever enqueue a job.
/// </summary>
internal sealed class ConnectionsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

    public FakeBackgroundJobClient BackgroundJobClient { get; } = new();

    public FakeConnectionsFinancialDataProvider FinancialDataProvider { get; } = new();

    public async Task<Guid> GetConnectionIdAsync(string pluggyItemId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
        var connection = await db.Connections.SingleAsync(c => c.PluggyItemId == pluggyItemId);
        return connection.Id;
    }

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
                ["Pluggy:ClientId"] = "test-client-id",
                ["Pluggy:ClientSecret"] = "test-client-secret",
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

            services.RemoveAll<IBackgroundJobClient>();
            services.AddSingleton<IBackgroundJobClient>(BackgroundJobClient);

            services.RemoveAll<IFinancialDataProvider>();
            services.AddSingleton<IFinancialDataProvider>(FinancialDataProvider);
        });
    }
}

/// <summary>
/// Full-control IFinancialDataProvider test double for this plan's endpoint tests (distinct from
/// Sync/Fakes.cs's FakeFinancialDataProvider, which always returns a fixed connect-token value --
/// insufficient here since PLG-02 requires proving token freshness across calls). Records every
/// CreateConnectTokenAsync/DeleteItemAsync call for direct assertion.
/// </summary>
internal sealed class FakeConnectionsFinancialDataProvider : IFinancialDataProvider
{
    public List<string?> ConnectTokenRequests { get; } = [];

    public List<string> DeleteItemCalls { get; } = [];

    public Dictionary<string, ProviderItemStatus> ItemStatusByItemId { get; } = [];

    public ProviderItemStatus DefaultItemStatus { get; set; } =
        new("UPDATED", "SUCCESS", null, null, null, 1, "TestConnector", "Test Bank", null);

    public Task<string> CreateConnectTokenAsync(string? existingPluggyItemId, CancellationToken ct)
    {
        ConnectTokenRequests.Add(existingPluggyItemId);
        return Task.FromResult($"token-{ConnectTokenRequests.Count}-{Guid.NewGuid():N}");
    }

    public Task<ProviderItemStatus> GetItemStatusAsync(string pluggyItemId, CancellationToken ct)
    {
        var status = ItemStatusByItemId.TryGetValue(pluggyItemId, out var s) ? s : DefaultItemStatus;
        return Task.FromResult(status);
    }

    public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string pluggyItemId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderAccount>>([]);

    public Task<IReadOnlyList<ProviderTransaction>> GetTransactionsAsync(string pluggyAccountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderTransaction>>([]);

    public Task<IReadOnlyList<ProviderInvestment>> GetInvestmentsAsync(string pluggyItemId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderInvestment>>([]);

    public Task TriggerItemUpdateAsync(string pluggyItemId, CancellationToken ct) => Task.CompletedTask;

    public Task DeleteItemAsync(string pluggyItemId, CancellationToken ct)
    {
        DeleteItemCalls.Add(pluggyItemId);
        return Task.CompletedTask;
    }

    public Task SubmitMfaAsync(string pluggyItemId, string parameterName, string parameterValue, CancellationToken ct) =>
        Task.CompletedTask;
}
