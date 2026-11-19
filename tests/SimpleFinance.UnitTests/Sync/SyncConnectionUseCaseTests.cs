using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Categorization;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Sync;

namespace SimpleFinance.UnitTests.Sync;

/// <summary>
/// Exercises SyncConnectionUseCase (the ONE shared reconciler, SYNC-04/SYNC-07) against EF Core
/// InMemory with a hand-built FakeFinancialDataProvider — matching Phase 1/02-03's established
/// test pattern. Task 1 covers Account/CreditCardDetail/Investment upsert idempotency and the
/// distributed-lock contract (via a no-op fake — the real Postgres-backed lock is exercised
/// separately in Task 3's ConcurrentSyncTests).
/// </summary>
public class SyncConnectionUseCaseTests
{
    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Connection SeedConnection(SimpleFinanceDbContext db, string pluggyItemId = "item-001")
    {
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PluggyItemId = pluggyItemId,
            Status = ConnectionStatus.Updated,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Connections.Add(connection);
        db.SaveChanges();
        return connection;
    }

    private static SyncConnectionUseCase CreateSut(
        SimpleFinanceDbContext db, FakeFinancialDataProvider provider, FakeTransferDetectionService? transferDetection = null) =>
        new(provider, db, new FakeDistributedLockProvider(), new FakeCategorizationService(), transferDetection ?? new FakeTransferDetectionService(), new NoOpBackgroundJobClient());

    private static SyncConnectionUseCase CreateSutWithRealCategorization(SimpleFinanceDbContext db, FakeFinancialDataProvider provider) =>
        new(provider, db, new FakeDistributedLockProvider(), new CategoryRuleEngine(db), new FakeTransferDetectionService(), new NoOpBackgroundJobClient());

    private static List<ProviderTransaction> ReadTransactionFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Sync", "Fixtures", fileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<ProviderTransaction>>(json)!;
    }

    [Fact]
    public async Task SyncConnectionAsync_NewAccountsAndInvestment_InsertsExactRowsAndReportsCounts()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var provider = new FakeFinancialDataProvider
        {
            Accounts =
            [
                new ProviderAccount("acc-1", "BANK", "CHECKING_ACCOUNT", "Checking", null, null, null, 1000m, "BRL", null),
                new ProviderAccount("acc-2", "BANK", "SAVINGS_ACCOUNT", "Savings", null, null, null, 5000m, "BRL", null),
            ],
            Investments =
            [
                new ProviderInvestment("inv-1", "Tesouro Selic 2029", "FIXED_INCOME", null, null, "Tesouro Nacional",
                    1000m, 1000m, 10m, 900m, 0.01m, 0.12m, DateTime.UtcNow, null, "BRL"),
            ],
        };
        var sut = CreateSut(db, provider);

        var result = await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(2, result.AccountsAdded);
        Assert.Equal(0, result.AccountsUpdated);
        Assert.Equal(1, result.InvestmentsAdded);
        Assert.Equal(0, result.InvestmentsUpdated);
        Assert.Equal(2, await db.Accounts.CountAsync());
        Assert.Equal(1, await db.Investments.CountAsync());
    }

    [Fact]
    public async Task SyncConnectionAsync_RunTwiceWithUnchangedData_SecondRunProducesZeroDelta()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var provider = new FakeFinancialDataProvider
        {
            Accounts =
            [
                new ProviderAccount("acc-1", "BANK", "CHECKING_ACCOUNT", "Checking", null, null, null, 1000m, "BRL", null),
            ],
            Investments =
            [
                new ProviderInvestment("inv-1", "Tesouro Selic 2029", "FIXED_INCOME", null, null, "Tesouro Nacional",
                    1000m, 1000m, 10m, 900m, 0.01m, 0.12m, DateTime.UtcNow, null, "BRL"),
            ],
        };
        var sut = CreateSut(db, provider);

        var first = await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);
        var second = await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(1, first.AccountsAdded);
        Assert.Equal(0, second.AccountsAdded);
        Assert.Equal(0, second.AccountsUpdated);
        Assert.Equal(0, second.InvestmentsAdded);
        Assert.Equal(0, second.InvestmentsUpdated);
        Assert.Equal(1, await db.Accounts.CountAsync());
        Assert.Equal(1, await db.Investments.CountAsync());
    }

    [Fact]
    public async Task SyncConnectionAsync_ChangedAccountBalance_UpdatesInPlaceNeverInserts()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var provider = new FakeFinancialDataProvider
        {
            Accounts =
            [
                new ProviderAccount("acc-1", "BANK", "CHECKING_ACCOUNT", "Checking", null, null, null, 1000m, "BRL", null),
            ],
        };
        var sut = CreateSut(db, provider);

        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);
        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        provider.Accounts =
        [
            new ProviderAccount("acc-1", "BANK", "CHECKING_ACCOUNT", "Checking", null, null, null, 1234.56m, "BRL", null),
        ];
        var third = await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(0, third.AccountsAdded);
        Assert.Equal(1, third.AccountsUpdated);
        Assert.Equal(1, await db.Accounts.CountAsync());
        var stored = await db.Accounts.SingleAsync();
        Assert.Equal(1234.56m, stored.Balance);
    }

    [Fact]
    public async Task SyncConnectionAsync_CreditAccountWithCreditData_UpsertsCreditCardDetailNeverDuplicates()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var provider = new FakeFinancialDataProvider
        {
            Accounts =
            [
                new ProviderAccount("acc-credit", "CREDIT", "CREDIT_CARD", "Cartao Platinum", null, null, null, -500m, "BRL",
                    new ProviderCreditData("VISA", "PLATINUM", null, 5000m, 4500m, null, null, null, "ACTIVE", "MAIN")),
            ],
        };
        var sut = CreateSut(db, provider);

        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);
        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(1, await db.CreditCardDetails.CountAsync());
        var detail = await db.CreditCardDetails.SingleAsync();
        Assert.Equal("VISA", detail.Brand);
        Assert.Equal("PLATINUM", detail.Level);
        Assert.Equal(CreditCardStatus.Active, detail.Status);
    }

    [Fact]
    public async Task SyncConnectionAsync_UpdatesConnectionStatusFieldsFromProvider()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var provider = new FakeFinancialDataProvider
        {
            ItemStatus = new ProviderItemStatus("OUTDATED", "PARTIAL_SUCCESS", "detail-x", "423", "quota exceeded", 1, "Bank", "Bank Inc", null),
        };
        var sut = CreateSut(db, provider);

        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        var stored = await db.Connections.SingleAsync(c => c.Id == connection.Id);
        Assert.Equal(ConnectionStatus.Outdated, stored.Status);
        Assert.Equal("PARTIAL_SUCCESS", stored.ExecutionStatus);
        Assert.Equal("423", stored.ErrorCode);
        Assert.NotNull(stored.LastSyncAt);
    }

    [Fact]
    public async Task SyncConnectionAsync_UnknownConnectionId_ThrowsConnectionNotFoundException()
    {
        await using var db = CreateDbContext();
        var provider = new FakeFinancialDataProvider();
        var sut = CreateSut(db, provider);

        await Assert.ThrowsAsync<ConnectionNotFoundException>(
            () => sut.SyncConnectionAsync(Guid.NewGuid(), SyncTrigger.Manual, CancellationToken.None));
    }

    // --- Task 2: transaction reconciliation (PENDING->POSTED ID-instability fallback,
    // categorization call site) ---

    private static FakeFinancialDataProvider CreateProviderWithBankAccount(List<ProviderTransaction> transactions) =>
        new()
        {
            Accounts =
            [
                new ProviderAccount("acc-bank-001", "BANK", "CHECKING_ACCOUNT", "Checking", null, null, null, 2000m, "BRL", null),
            ],
            TransactionsByAccountId = new Dictionary<string, List<ProviderTransaction>>
            {
                ["acc-bank-001"] = transactions,
            },
        };

    [Fact]
    public async Task SyncConnectionAsync_NewTransaction_CategorizesAndStoresCategoryFromMapping()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var categoryId = Guid.NewGuid();
        db.CategoryMappings.Add(new CategoryMapping
        {
            Id = Guid.NewGuid(),
            PluggyCategoryId = "cat-groceries",
            PluggyCategoryName = "Groceries",
            CategoryId = categoryId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var provider = CreateProviderWithBankAccount(ReadTransactionFixture("provider-transactions-initial.json"));
        var sut = CreateSutWithRealCategorization(db, provider);

        var result = await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(3, result.TransactionsAdded);
        Assert.Equal(3, await db.Transactions.CountAsync());

        var groceries = await db.Transactions.SingleAsync(t => t.PluggyTransactionId == "txn-001");
        Assert.Equal(categoryId, groceries.CategoryId);
        Assert.Equal(CategorySource.SystemMapping, groceries.CategorySource);

        var uncategorized = await db.Transactions.SingleAsync(t => t.PluggyTransactionId == "txn-002");
        Assert.Null(uncategorized.CategoryId);
    }

    [Fact]
    public async Task SyncConnectionAsync_RunTwiceIdenticalTransactions_SecondRunProducesZeroDelta()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var provider = CreateProviderWithBankAccount(ReadTransactionFixture("provider-transactions-initial.json"));
        var sut = CreateSutWithRealCategorization(db, provider);

        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);
        provider = CreateProviderWithBankAccount(ReadTransactionFixture("provider-transactions-initial.json"));
        var sutSecondRun = CreateSutWithRealCategorization(db, provider);
        var second = await sutSecondRun.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(0, second.TransactionsAdded);
        Assert.Equal(0, second.TransactionsUpdated);
        Assert.Equal(3, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task SyncConnectionAsync_SamePluggyId_StatusChangesPendingToPosted_UpdatesInPlace()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var pendingTxn = new ProviderTransaction(
            "txn-same-id", "acc-bank-001", new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            "Loja ABC", "LOJA ABC", -200m, -200m, null, "BRL", "PENDING", "DEBIT",
            null, null, null, null, null, null, null, null, null);
        var provider = CreateProviderWithBankAccount([pendingTxn]);
        var sut = CreateSutWithRealCategorization(db, provider);
        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        var postedTxn = pendingTxn with { Status = "POSTED", BillId = "bill-999" };
        var provider2 = CreateProviderWithBankAccount([postedTxn]);
        var sut2 = CreateSutWithRealCategorization(db, provider2);
        var second = await sut2.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(0, second.TransactionsAdded);
        Assert.Equal(1, second.TransactionsUpdated);
        Assert.Equal(1, await db.Transactions.CountAsync());
        var stored = await db.Transactions.SingleAsync();
        Assert.Equal("txn-same-id", stored.PluggyTransactionId);
        Assert.Equal(TransactionStatus.Posted, stored.Status);
        Assert.Equal("bill-999", stored.BillId);
    }

    [Fact]
    public async Task SyncConnectionAsync_FallbackMatch_DifferentIdSameLogicalTransaction_UpdatesInPlaceNeverDuplicates()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var provider = CreateProviderWithBankAccount(ReadTransactionFixture("provider-transactions-initial.json"));
        var sut = CreateSutWithRealCategorization(db, provider);
        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        var provider2 = CreateProviderWithBankAccount(ReadTransactionFixture("provider-transactions-pending-to-posted.json"));
        var sut2 = CreateSutWithRealCategorization(db, provider2);
        var second = await sut2.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(0, second.TransactionsAdded);
        Assert.Equal(1, second.TransactionsUpdated); // only the fallback-matched row changed
        Assert.Equal(3, await db.Transactions.CountAsync()); // never 4 -- no duplicate inserted

        var matched = await db.Transactions.SingleAsync(t => t.PluggyTransactionId == "txn-003-posted-different-id");
        Assert.Equal(TransactionStatus.Posted, matched.Status);
        Assert.Equal("bill-123", matched.BillId);
        Assert.False(await db.Transactions.AnyAsync(t => t.PluggyTransactionId == "txn-003-pending"));
    }

    [Fact]
    public async Task SyncConnectionAsync_NoMatch_InsertsAsNewNeverIncorrectlyMerged()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var pendingTxn = new ProviderTransaction(
            "txn-original", "acc-bank-001", new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            "Foo Store", "FOO STORE", -100m, -100m, null, "BRL", "PENDING", "DEBIT",
            null, null, null, null, null, null, null, null, null);
        var provider = CreateProviderWithBankAccount([pendingTxn]);
        var sut = CreateSutWithRealCategorization(db, provider);
        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        // Different id, different amount, different description -- no match on any axis.
        var unrelatedTxn = new ProviderTransaction(
            "txn-unrelated", "acc-bank-001", new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc),
            "Bar Cafe", "BAR CAFE", -999m, -999m, null, "BRL", "POSTED", "DEBIT",
            null, null, null, null, null, null, null, null, null);
        var provider2 = CreateProviderWithBankAccount([pendingTxn, unrelatedTxn]);
        var sut2 = CreateSutWithRealCategorization(db, provider2);
        var second = await sut2.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(1, second.TransactionsAdded);
        Assert.Equal(0, second.TransactionsUpdated);
        Assert.Equal(2, await db.Transactions.CountAsync());

        var original = await db.Transactions.SingleAsync(t => t.PluggyTransactionId == "txn-original");
        Assert.Equal(TransactionStatus.Pending, original.Status); // untouched, never merged into
    }

    [Fact]
    public async Task SyncConnectionAsync_AmbiguousFallbackCandidates_NeverGuessesInsertsNewInstead()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var date = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        var candidateA = new ProviderTransaction(
            "txn-candidate-a", "acc-bank-001", date, "Same Desc", "SAME DESC", -50m, -50m, null, "BRL", "PENDING", "DEBIT",
            null, null, null, null, null, null, null, null, null);
        var candidateB = candidateA with { Id = "txn-candidate-b" };
        var provider = CreateProviderWithBankAccount([candidateA, candidateB]);
        var sut = CreateSutWithRealCategorization(db, provider);
        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);
        Assert.Equal(2, await db.Transactions.CountAsync());

        // A third transaction with a different id but the SAME amount/date/description as BOTH
        // existing Pending rows -- ambiguous, must insert new rather than guessing which to merge.
        var incoming = candidateA with { Id = "txn-incoming", Status = "POSTED" };
        var provider2 = CreateProviderWithBankAccount([candidateA, candidateB, incoming]);
        var sut2 = CreateSutWithRealCategorization(db, provider2);
        var second = await sut2.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        Assert.Equal(1, second.TransactionsAdded);
        Assert.Equal(0, second.TransactionsUpdated);
        Assert.Equal(3, await db.Transactions.CountAsync());
        Assert.Equal(2, await db.Transactions.CountAsync(t => t.Status == TransactionStatus.Pending));
    }

    [Fact]
    public async Task SyncConnectionAsync_ExistingRowWithManualOverride_ResyncNeverTouchesCategoryEvenIfNewRuleWouldMatch()
    {
        // 04-04's regression lock: ReconcileOneTransactionAsync's existing-row path
        // (ApplyTransactionFields) must never call ICategorizationService and must never touch
        // CategoryId/CategorySource -- a user's manual recategorization (CategorySource.
        // UserOverride, set by 03-05's PATCH /transactions/{id}/category) is sacred and must
        // survive re-sync even when a newly-added CategoryRule would now match the same
        // transaction (04-CONTEXT.md's "Manual overrides are sacred" specific).
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var initialTxn = new ProviderTransaction(
            "txn-manual-override", "acc-bank-001", new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            "Padaria do Bairro", "PADARIA DO BAIRRO", -25.50m, -25.50m, null, "BRL", "POSTED", "DEBIT",
            null, null, null, null, null, null, null, null, null);
        var provider = CreateProviderWithBankAccount([initialTxn]);
        var sut = CreateSutWithRealCategorization(db, provider);
        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        var manualCategoryId = Guid.NewGuid();
        var stored = await db.Transactions.SingleAsync(t => t.PluggyTransactionId == "txn-manual-override");
        stored.CategoryId = manualCategoryId;
        stored.CategorySource = CategorySource.UserOverride;
        await db.SaveChangesAsync();

        // A rule that would now match this exact transaction's description, added AFTER the
        // manual override -- must never be consulted for an already-existing row.
        db.CategoryRules.Add(new CategoryRule
        {
            Id = Guid.NewGuid(),
            UserId = connection.UserId,
            MatchField = RuleMatchField.Description,
            MatchOperator = RuleMatchOperator.Contains,
            MatchValue = "padaria",
            CategoryId = Guid.NewGuid(),
            Priority = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var provider2 = CreateProviderWithBankAccount([initialTxn with { BillId = "bill-updated" }]);
        var sut2 = CreateSutWithRealCategorization(db, provider2);
        await sut2.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        var afterResync = await db.Transactions.SingleAsync(t => t.PluggyTransactionId == "txn-manual-override");
        Assert.Equal(manualCategoryId, afterResync.CategoryId);
        Assert.Equal(CategorySource.UserOverride, afterResync.CategorySource);
        Assert.Equal("bill-updated", afterResync.BillId); // confirms the existing-row path DID run
    }

    // --- Task 3: SyncLog auditing (SYNC-05) ---

    [Fact]
    public async Task SyncConnectionAsync_Success_WritesExactlyOneSyncLogRowWithAccurateCounts()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var provider = new FakeFinancialDataProvider
        {
            Accounts =
            [
                new ProviderAccount("acc-1", "BANK", "CHECKING_ACCOUNT", "Checking", null, null, null, 1000m, "BRL", null),
            ],
        };
        var sut = CreateSut(db, provider);
        var beforeStart = DateTime.UtcNow;

        var result = await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Cron, CancellationToken.None);

        var log = await db.SyncLogs.SingleAsync(l => l.ConnectionId == connection.Id);
        Assert.Equal(SyncTrigger.Cron, log.TriggeredBy);
        Assert.Equal(SyncStatus.Success, log.Status);
        Assert.True(log.StartedAt >= beforeStart);
        Assert.NotNull(log.FinishedAt);
        Assert.True(log.FinishedAt >= log.StartedAt);
        Assert.Null(log.Error);
        Assert.Equal(result.AccountsAdded, log.AccountsAdded);
        Assert.Equal(1, log.AccountsAdded);
    }

    [Fact]
    public async Task SyncConnectionAsync_ProviderThrows_WritesFailedSyncLogAndRethrowsOriginalException()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var provider = new FakeFinancialDataProvider
        {
            ThrowOnGetAccounts = new InvalidOperationException("simulated provider failure"),
        };
        var sut = CreateSut(db, provider);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SyncConnectionAsync(connection.Id, SyncTrigger.Webhook, CancellationToken.None));
        Assert.Equal("simulated provider failure", thrown.Message);

        var log = await db.SyncLogs.SingleAsync(l => l.ConnectionId == connection.Id);
        Assert.Equal(SyncStatus.Failed, log.Status);
        Assert.Equal("simulated provider failure", log.Error);
        Assert.NotNull(log.FinishedAt);

        // Failure must not leave partial Account/Transaction/Investment writes behind.
        Assert.Equal(0, await db.Accounts.CountAsync());
    }

    // --- 03-02 Task 2: post-sync transfer-detection hook ---

    [Fact]
    public async Task SyncConnectionAsync_SuccessfulRun_CallsTransferDetectionExactlyOnceWithConnectionUserId()
    {
        await using var db = CreateDbContext();
        var connection = SeedConnection(db);
        var provider = new FakeFinancialDataProvider
        {
            Accounts =
            [
                new ProviderAccount("acc-1", "BANK", "CHECKING_ACCOUNT", "Checking", null, null, null, 1000m, "BRL", null),
            ],
        };
        var transferDetection = new FakeTransferDetectionService();
        var sut = CreateSut(db, provider, transferDetection);

        await sut.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None);

        Assert.Single(transferDetection.CalledWithUserIds);
        Assert.Equal(connection.UserId, transferDetection.CalledWithUserIds[0]);
    }
}
