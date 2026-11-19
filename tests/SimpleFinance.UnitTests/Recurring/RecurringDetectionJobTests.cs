using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Recurring;

namespace SimpleFinance.UnitTests.Recurring;

/// <summary>
/// RECUR-01/RECUR-04 (07-03-PLAN.md Task 1): RecurringDetectionJob is pure EF Core (no Dapper),
/// so these tests use EF Core's InMemory provider -- mirrors DailySyncJobTests.cs's pattern, not
/// NetWorthSnapshotJobTests.cs's real-Postgres pattern (that one needs Dapper via DashboardQueries;
/// this job never does).
/// </summary>
public class RecurringDetectionJobTests
{
    private static SimpleFinanceDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static RecurringOptions DefaultOptions() => new();

    private static IOptions<RecurringOptions> WrapOptions(RecurringOptions options) => Options.Create(options);

    private static User NewUser(string email) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test User",
        Email = email,
        PasswordHash = "hash",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Transaction NewTransaction(
        Guid userId, DateTime date, decimal amount, string merchantName,
        TransactionStatus status = TransactionStatus.Posted,
        TransactionType type = TransactionType.Debit,
        bool isTransfer = false,
        bool isHidden = false) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AccountId = Guid.NewGuid(),
        PluggyTransactionId = $"txn-{Guid.NewGuid():N}",
        Date = date,
        Description = merchantName,
        Amount = amount,
        Type = type,
        CurrencyCode = "BRL",
        Status = status,
        MerchantName = merchantName,
        IsTransfer = isTransfer,
        IsHidden = isHidden,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task ExecuteAsync_ThreeMonthlySameMerchantSameAmount_ProducesExactlyOneRecurringRow()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDb();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        db.Transactions.AddRange(
            NewTransaction(userId, DateTime.UtcNow.AddDays(-90), 39.90m, "NETFLIX.COM"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-60), 39.90m, "NETFLIX.COM"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-30), 39.90m, "NETFLIX.COM"));
        await db.SaveChangesAsync();

        var job = new RecurringDetectionJob(db, NullLogger<RecurringDetectionJob>.Instance, WrapOptions(DefaultOptions()));

        await job.ExecuteAsync(CancellationToken.None);

        var recurring = await db.RecurringTransactions.Where(r => r.UserId == userId).ToListAsync();
        var row = Assert.Single(recurring);
        Assert.Equal(RecurringFrequency.Monthly, row.Frequency);
        Assert.Equal(39.90m, row.AverageAmount);
    }

    [Fact]
    public async Task ExecuteAsync_CalledTwiceUnchangedData_ExactlyOneRow_SameId_NotDuplicated()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDb();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        db.Transactions.AddRange(
            NewTransaction(userId, DateTime.UtcNow.AddDays(-90), 39.90m, "NETFLIX.COM"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-60), 39.90m, "NETFLIX.COM"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-30), 39.90m, "NETFLIX.COM"));
        await db.SaveChangesAsync();

        var job = new RecurringDetectionJob(db, NullLogger<RecurringDetectionJob>.Instance, WrapOptions(DefaultOptions()));

        await job.ExecuteAsync(CancellationToken.None);
        var firstRunId = (await db.RecurringTransactions.SingleAsync(r => r.UserId == userId)).Id;

        await job.ExecuteAsync(CancellationToken.None);

        var recurring = await db.RecurringTransactions.Where(r => r.UserId == userId).ToListAsync();
        var row = Assert.Single(recurring);
        Assert.Equal(firstRunId, row.Id);
    }

    [Fact]
    public async Task ExecuteAsync_FirstDetection_IsNewlyDetectedTrue_SecondRun_IsNewlyDetectedFalse()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDb();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        db.Transactions.AddRange(
            NewTransaction(userId, DateTime.UtcNow.AddDays(-90), 39.90m, "NETFLIX.COM"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-60), 39.90m, "NETFLIX.COM"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-30), 39.90m, "NETFLIX.COM"));
        await db.SaveChangesAsync();

        var job = new RecurringDetectionJob(db, NullLogger<RecurringDetectionJob>.Instance, WrapOptions(DefaultOptions()));

        await job.ExecuteAsync(CancellationToken.None);
        var afterFirstRun = await db.RecurringTransactions.SingleAsync(r => r.UserId == userId);
        Assert.True(afterFirstRun.IsNewlyDetected);

        await job.ExecuteAsync(CancellationToken.None);
        var afterSecondRun = await db.RecurringTransactions.SingleAsync(r => r.UserId == userId);
        Assert.False(afterSecondRun.IsNewlyDetected);
    }

    [Fact]
    public async Task ExecuteAsync_ReRunWithAmountAboveThreshold_SetsAmountIncreaseDetected_AndPreviousAverageAmount()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDb();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        db.Transactions.AddRange(
            NewTransaction(userId, DateTime.UtcNow.AddDays(-90), 100m, "GYM CO"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-60), 100m, "GYM CO"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-30), 100m, "GYM CO"));
        await db.SaveChangesAsync();

        var job = new RecurringDetectionJob(db, NullLogger<RecurringDetectionJob>.Instance, WrapOptions(DefaultOptions()));
        await job.ExecuteAsync(CancellationToken.None);

        // Default AmountIncreaseThresholdPercent = 0.15m -- a jump from 100 to 130 is a 30%
        // increase, well beyond threshold. REPLACE (not add alongside) the old transactions --
        // the job re-queries the FULL lookback window every run, so leaving the old 100m rows in
        // place would dilute RecurringDetector's amount-tolerance band across both price points
        // and drop the group entirely (below MinOccurrences after tolerance filtering).
        var oldTransactions = await db.Transactions.Where(t => t.UserId == userId).ToListAsync();
        db.Transactions.RemoveRange(oldTransactions);
        db.Transactions.AddRange(
            NewTransaction(userId, DateTime.UtcNow.AddDays(-90), 130m, "GYM CO"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-60), 130m, "GYM CO"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-30), 130m, "GYM CO"));
        await db.SaveChangesAsync();

        await job.ExecuteAsync(CancellationToken.None);

        var row = await db.RecurringTransactions.SingleAsync(r => r.UserId == userId);
        Assert.True(row.AmountIncreaseDetected);
        Assert.Equal(100m, row.PreviousAverageAmount);
    }

    [Fact]
    public async Task ExecuteAsync_ReRunWithAmountWithinThreshold_AmountIncreaseDetectedFalse()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDb();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        db.Transactions.AddRange(
            NewTransaction(userId, DateTime.UtcNow.AddDays(-90), 100m, "GYM CO"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-60), 100m, "GYM CO"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-30), 100m, "GYM CO"));
        await db.SaveChangesAsync();

        var job = new RecurringDetectionJob(db, NullLogger<RecurringDetectionJob>.Instance, WrapOptions(DefaultOptions()));
        await job.ExecuteAsync(CancellationToken.None);

        // Small 5% bump -- well within the default 15% threshold. Replace (not add alongside) for
        // the same reason as the amount-increase test above.
        var oldTransactions = await db.Transactions.Where(t => t.UserId == userId).ToListAsync();
        db.Transactions.RemoveRange(oldTransactions);
        db.Transactions.AddRange(
            NewTransaction(userId, DateTime.UtcNow.AddDays(-90), 105m, "GYM CO"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-60), 105m, "GYM CO"),
            NewTransaction(userId, DateTime.UtcNow.AddDays(-30), 105m, "GYM CO"));
        await db.SaveChangesAsync();

        await job.ExecuteAsync(CancellationToken.None);

        var row = await db.RecurringTransactions.SingleAsync(r => r.UserId == userId);
        Assert.False(row.AmountIncreaseDetected);
    }

    [Fact]
    public async Task ExecuteAsync_MemberTransactions_GetRecurringTransactionIdSet()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDb();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        var t1 = NewTransaction(userId, DateTime.UtcNow.AddDays(-90), 39.90m, "NETFLIX.COM");
        var t2 = NewTransaction(userId, DateTime.UtcNow.AddDays(-60), 39.90m, "NETFLIX.COM");
        var t3 = NewTransaction(userId, DateTime.UtcNow.AddDays(-30), 39.90m, "NETFLIX.COM");
        db.Transactions.AddRange(t1, t2, t3);
        await db.SaveChangesAsync();

        var job = new RecurringDetectionJob(db, NullLogger<RecurringDetectionJob>.Instance, WrapOptions(DefaultOptions()));
        await job.ExecuteAsync(CancellationToken.None);

        var recurringId = (await db.RecurringTransactions.SingleAsync(r => r.UserId == userId)).Id;

        var reloaded1 = await db.Transactions.SingleAsync(t => t.Id == t1.Id);
        var reloaded2 = await db.Transactions.SingleAsync(t => t.Id == t2.Id);
        var reloaded3 = await db.Transactions.SingleAsync(t => t.Id == t3.Id);

        Assert.Equal(recurringId, reloaded1.RecurringTransactionId);
        Assert.Equal(recurringId, reloaded2.RecurringTransactionId);
        Assert.Equal(recurringId, reloaded3.RecurringTransactionId);
    }

    [Fact]
    public async Task ExecuteAsync_OneUsersFailureIsolated_OtherUsersRecurringStillWritten_BatchNeverAborts()
    {
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();

        await using var db = new ThrowOnSaveForUserDbContext(
            new DbContextOptionsBuilder<SimpleFinanceDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            throwForUserId: user1Id);

        var user1 = NewUser($"{Guid.NewGuid():N}@example.com");
        user1.Id = user1Id;
        var user2 = NewUser($"{Guid.NewGuid():N}@example.com");
        user2.Id = user2Id;
        db.Users.AddRange(user1, user2);
        db.Transactions.AddRange(
            NewTransaction(user1Id, DateTime.UtcNow.AddDays(-90), 39.90m, "NETFLIX.COM"),
            NewTransaction(user1Id, DateTime.UtcNow.AddDays(-60), 39.90m, "NETFLIX.COM"),
            NewTransaction(user1Id, DateTime.UtcNow.AddDays(-30), 39.90m, "NETFLIX.COM"),
            NewTransaction(user2Id, DateTime.UtcNow.AddDays(-90), 19.90m, "SPOTIFY"),
            NewTransaction(user2Id, DateTime.UtcNow.AddDays(-60), 19.90m, "SPOTIFY"),
            NewTransaction(user2Id, DateTime.UtcNow.AddDays(-30), 19.90m, "SPOTIFY"));
        db.DisableThrow = true;
        await db.SaveChangesAsync();
        db.DisableThrow = false;

        var job = new RecurringDetectionJob(db, NullLogger<RecurringDetectionJob>.Instance, WrapOptions(DefaultOptions()));

        // Must not throw -- user1's simulated save failure is caught and logged, never allowed to
        // abort the batch. User2's recurring row must still be written.
        await job.ExecuteAsync(CancellationToken.None);

        var user1Recurring = await db.RecurringTransactions.SingleOrDefaultAsync(r => r.UserId == user1Id);
        var user2Recurring = await db.RecurringTransactions.SingleOrDefaultAsync(r => r.UserId == user2Id);

        Assert.Null(user1Recurring);
        Assert.NotNull(user2Recurring);
    }

    /// <summary>
    /// Test-only DbContext subclass that throws on SaveChangesAsync whenever a tracked
    /// RecurringTransaction for a specific UserId is about to be persisted -- simulates a
    /// transient per-user DB failure, mirroring NetWorthSnapshotJobTests.cs's
    /// ThrowOnSaveForUserDbContext exactly, applied to RecurringTransaction instead of
    /// NetWorthSnapshot.
    /// </summary>
    private sealed class ThrowOnSaveForUserDbContext(
        DbContextOptions<SimpleFinanceDbContext> options, Guid throwForUserId)
        : SimpleFinanceDbContext(options)
    {
        private readonly Guid _throwForUserId = throwForUserId;

        public bool DisableThrow { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!DisableThrow && ChangeTracker.Entries<RecurringTransaction>()
                    .Any(e => e.Entity.UserId == _throwForUserId
                        && (e.State == EntityState.Added || e.State == EntityState.Modified)))
            {
                throw new InvalidOperationException("simulated per-user recurring-transaction save failure");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
