using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Alerts;
using SimpleFinance.Infrastructure.Budgets;
using SimpleFinance.Infrastructure.Goals;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.UnitTests.Alerts;

/// <summary>
/// Postgres-backed integration tests for AlertEvaluationJob (08-04, NOTIF-02/NOTIF-03) --
/// AlertEvaluationJob delegates to BudgetService/GoalService, which themselves depend on
/// DashboardQueries' Dapper queries and require a REAL Postgres connection (EF Core's InMemory
/// provider has no ADO.NET connection behind it for Dapper to use). Same connection-string-
/// resolution pattern as BudgetServiceTests.cs/NetWorthSnapshotJobTests.cs: ConnectionStrings__Test
/// env var, else the docker-compose default at 127.0.0.1:5433. Requires `docker compose up
/// postgres -d` running locally first.
///
/// Every test scopes its rows to fresh Guid User ids, so tests can run against the same shared
/// database without interfering with each other or requiring cleanup (matches
/// BudgetServiceTests.cs/NetWorthSnapshotJobTests.cs precedent). Dispatch itself is never
/// asserted here (NotificationDispatcherTests.cs already covers DispatchAsync in isolation) --
/// with zero Notifications__* configured, every channel's IsConfigured is false and
/// DispatchAsync no-ops, so calling the real NotificationDispatcher here is safe and never
/// reaches out to a real Telegram/SMTP endpoint.
/// </summary>
public class AlertEvaluationJobTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static DashboardQueries CreateDashboardQueries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = GetConnectionString(),
            })
            .Build();

        return new DashboardQueries(new NpgsqlDbConnectionFactory(configuration));
    }

    private static NotificationDispatcher CreateDispatcher()
    {
        // Zero Notifications__* configured -- every channel's IsConfigured is false, so
        // DispatchAsync no-ops for every call. Safe to use the real dispatcher/channels here.
        var options = Options.Create(new NotificationOptions());
        var channels = new INotificationChannel[]
        {
            new TelegramChannel(options, NullLogger<TelegramChannel>.Instance),
            new EmailChannel(options, NullLogger<EmailChannel>.Instance),
        };
        return new NotificationDispatcher(channels, NullLogger<NotificationDispatcher>.Instance);
    }

    private static AlertEvaluationJob CreateJob(SimpleFinanceDbContext db, int largeTransactionLookbackDays = 3) =>
        new(
            db,
            new BudgetService(db, CreateDashboardQueries()),
            new GoalService(db),
            CreateDispatcher(),
            Options.Create(new AlertEvaluationOptions { LargeTransactionLookbackDays = largeTransactionLookbackDays }),
            NullLogger<AlertEvaluationJob>.Instance);

    private static User NewUser(string email) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test User",
        Email = email,
        PasswordHash = "hash",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Account NewAccount(
        Guid userId, AccountType type, decimal balance, bool isHidden = false) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConnectionId = Guid.NewGuid(),
        PluggyAccountId = $"acct-{Guid.NewGuid():N}",
        Type = type,
        Subtype = type == AccountType.Credit ? AccountSubtype.CreditCard : AccountSubtype.CheckingAccount,
        Name = "Test Account",
        Balance = balance,
        CurrencyCode = "BRL",
        IsHidden = isHidden,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Category NewCategory(Guid userId, string name) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Name = name,
        Type = CategoryType.Expense,
        Icon = "icon",
        Color = "#000000",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Transaction NewTransaction(
        Guid userId, Guid accountId, DateTime date, decimal amount, TransactionType type,
        TransactionStatus status = TransactionStatus.Posted, bool isTransfer = false, bool isHidden = false,
        Guid? categoryId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AccountId = accountId,
        PluggyTransactionId = $"txn-{Guid.NewGuid():N}",
        Date = date,
        Description = "Test Transaction",
        Amount = amount,
        Type = type,
        CurrencyCode = "BRL",
        Status = status,
        CategorySource = CategorySource.SystemMapping,
        CategoryId = categoryId,
        IsTransfer = isTransfer,
        IsHidden = isHidden,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Budget NewBudget(Guid userId, Guid? categoryId, int year, int month, decimal limitAmount) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CategoryId = categoryId,
        Year = year,
        Month = month,
        LimitAmount = limitAmount,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static CreditCardDetail NewCreditCardDetail(Guid accountId, DateTime? balanceDueDate) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        BalanceDueDate = balanceDueDate,
        Status = CreditCardStatus.Active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static AlertRule NewAlertRule(
        Guid userId, AlertRuleType type, decimal? threshold = null, Guid? categoryId = null,
        Guid? accountId = null, int? daysBefore = null, NotificationChannelType channel = NotificationChannelType.Telegram,
        bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Type = type,
        Threshold = threshold,
        CategoryId = categoryId,
        AccountId = accountId,
        DaysBefore = daysBefore,
        Channel = channel,
        IsActive = isActive,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static RecurringTransaction NewRecurringTransaction(
        Guid userId, bool isNewlyDetected, bool amountIncreaseDetected, decimal averageAmount,
        decimal? previousAverageAmount = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        NormalizedMerchant = $"MERCHANT-{Guid.NewGuid():N}",
        DisplayName = "Test Subscription",
        AverageAmount = averageAmount,
        PreviousAverageAmount = previousAverageAmount,
        Frequency = RecurringFrequency.Monthly,
        FirstSeenDate = DateTime.UtcNow.AddMonths(-3),
        LastSeenDate = DateTime.UtcNow,
        NextExpectedDate = DateTime.UtcNow.AddMonths(1),
        OccurrenceCount = 3,
        IsSubscription = false,
        IsActive = true,
        IsNewlyDetected = isNewlyDetected,
        AmountIncreaseDetected = amountIncreaseDetected,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Connection NewConnection(Guid userId, ConnectionStatus status) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        PluggyItemId = $"item-{Guid.NewGuid():N}",
        InstitutionName = "Test Bank",
        Status = status,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Goal NewGoal(
        Guid userId, GoalStatus status, decimal targetAmount, decimal currentAmount,
        Guid? linkedAccountId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Name = "Test Goal",
        TargetAmount = targetAmount,
        CurrentAmount = currentAmount,
        LinkedAccountId = linkedAccountId,
        Status = status,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ---- Test 1: BudgetThreshold ----

    [Fact]
    public async Task ExecuteAsync_BudgetSpendAt95Percent_Fires90TierNotification()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var midMonth = new DateTime(now.Year, now.Month, 1, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        var category = NewCategory(userId, "Groceries");
        var account = NewAccount(userId, AccountType.Bank, 1000m);
        db.Categories.Add(category);
        db.Accounts.Add(account);
        db.Budgets.Add(NewBudget(userId, category.Id, now.Year, now.Month, 1000m));
        db.Transactions.Add(NewTransaction(userId, account.Id, midMonth, 950m, TransactionType.Debit, categoryId: category.Id));
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.BudgetThreshold, categoryId: category.Id));
        await db.SaveChangesAsync();

        await CreateJob(db).ExecuteAsync(CancellationToken.None);

        var notification = Assert.Single(await db.Notifications.Where(n => n.UserId == userId).ToListAsync());
        Assert.Equal(AlertRuleType.BudgetThreshold, notification.Type);
        Assert.Contains("Groceries", notification.Message);
        Assert.Contains("90%", notification.Title);
    }

    [Fact]
    public async Task ExecuteAsync_BudgetSpendAt105Percent_Fires100TierOnly_NeverBothTiers()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var midMonth = new DateTime(now.Year, now.Month, 1, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        var category = NewCategory(userId, "Groceries");
        var account = NewAccount(userId, AccountType.Bank, 1000m);
        db.Categories.Add(category);
        db.Accounts.Add(account);
        db.Budgets.Add(NewBudget(userId, category.Id, now.Year, now.Month, 1000m));
        db.Transactions.Add(NewTransaction(userId, account.Id, midMonth, 1050m, TransactionType.Debit, categoryId: category.Id));
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.BudgetThreshold, categoryId: category.Id));
        await db.SaveChangesAsync();

        await CreateJob(db).ExecuteAsync(CancellationToken.None);

        var notification = Assert.Single(await db.Notifications.Where(n => n.UserId == userId).ToListAsync());
        Assert.Equal(AlertRuleType.BudgetThreshold, notification.Type);
        Assert.Contains("100%", notification.Title);
    }

    // ---- Test 2: de-dupe (BudgetThreshold) ----

    [Fact]
    public async Task ExecuteAsync_CalledTwiceForSameFixture_CreatesExactlyOneNotification_DeDupeIsReal()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var midMonth = new DateTime(now.Year, now.Month, 1, 12, 0, 0, DateTimeKind.Utc);

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        var category = NewCategory(userId, "Groceries");
        var account = NewAccount(userId, AccountType.Bank, 1000m);
        db.Categories.Add(category);
        db.Accounts.Add(account);
        db.Budgets.Add(NewBudget(userId, category.Id, now.Year, now.Month, 1000m));
        db.Transactions.Add(NewTransaction(userId, account.Id, midMonth, 950m, TransactionType.Debit, categoryId: category.Id));
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.BudgetThreshold, categoryId: category.Id));
        await db.SaveChangesAsync();

        var job = CreateJob(db);
        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);

        var count = await db.Notifications.CountAsync(n => n.UserId == userId);
        Assert.Equal(1, count);
    }

    // ---- Test 3: LowBalance ----

    [Fact]
    public async Task ExecuteAsync_AccountBelowThreshold_FiresLowBalanceNotification()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        var account = NewAccount(userId, AccountType.Bank, 50m);
        db.Accounts.Add(account);
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.LowBalance, threshold: 100m, accountId: account.Id));
        await db.SaveChangesAsync();

        await CreateJob(db).ExecuteAsync(CancellationToken.None);

        var notification = Assert.Single(await db.Notifications.Where(n => n.UserId == userId).ToListAsync());
        Assert.Equal(AlertRuleType.LowBalance, notification.Type);
    }

    [Fact]
    public async Task ExecuteAsync_AccountAboveThreshold_NoLowBalanceNotification()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        var account = NewAccount(userId, AccountType.Bank, 150m);
        db.Accounts.Add(account);
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.LowBalance, threshold: 100m, accountId: account.Id));
        await db.SaveChangesAsync();

        await CreateJob(db).ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, await db.Notifications.CountAsync(n => n.UserId == userId));
    }

    // ---- Test 4: InvoiceDue ----

    [Fact]
    public async Task ExecuteAsync_CardDueWithinDaysBefore_FiresInvoiceDueNotification()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        var account = NewAccount(userId, AccountType.Credit, 0m);
        db.Accounts.Add(account);
        db.CreditCardDetails.Add(NewCreditCardDetail(account.Id, DateTime.UtcNow.Date.AddDays(2)));
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.InvoiceDue, daysBefore: 3));
        await db.SaveChangesAsync();

        await CreateJob(db).ExecuteAsync(CancellationToken.None);

        var notification = Assert.Single(await db.Notifications.Where(n => n.UserId == userId).ToListAsync());
        Assert.Equal(AlertRuleType.InvoiceDue, notification.Type);
    }

    [Fact]
    public async Task ExecuteAsync_CardDueOutsideDaysBefore_NoInvoiceDueNotification()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        var account = NewAccount(userId, AccountType.Credit, 0m);
        db.Accounts.Add(account);
        db.CreditCardDetails.Add(NewCreditCardDetail(account.Id, DateTime.UtcNow.Date.AddDays(10)));
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.InvoiceDue, daysBefore: 3));
        await db.SaveChangesAsync();

        await CreateJob(db).ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, await db.Notifications.CountAsync(n => n.UserId == userId));
    }

    // ---- Test 5: LargeTransaction ----

    [Fact]
    public async Task ExecuteAsync_LargeTransaction_FiresOnce_NeverAgainOnRerun()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        var account = NewAccount(userId, AccountType.Bank, 1000m);
        db.Accounts.Add(account);
        db.Transactions.Add(NewTransaction(
            userId, account.Id, DateTime.UtcNow.AddDays(-1), -5000m, TransactionType.Debit));
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.LargeTransaction, threshold: 1000m));
        await db.SaveChangesAsync();

        var job = CreateJob(db);
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == userId));

        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == userId));
    }

    // ---- Test 6: per-user isolation ----

    [Fact]
    public async Task ExecuteAsync_OneUsersEvaluationThrows_OtherUsersNotificationStillCreated_BatchNeverAborts()
    {
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();

        await using var db = new ThrowOnSaveForUserDbContext(
            new DbContextOptionsBuilder<SimpleFinanceDbContext>().UseNpgsql(GetConnectionString(), vec => vec.UseVector()).Options,
            throwForUserId: user1Id);

        var user1 = NewUser($"{Guid.NewGuid():N}@example.com");
        user1.Id = user1Id;
        var user2 = NewUser($"{Guid.NewGuid():N}@example.com");
        user2.Id = user2Id;
        db.Users.AddRange(user1, user2);
        var account1 = NewAccount(user1Id, AccountType.Bank, 50m);
        var account2 = NewAccount(user2Id, AccountType.Bank, 50m);
        db.Accounts.AddRange(account1, account2);
        db.AlertRules.AddRange(
            NewAlertRule(user1Id, AlertRuleType.LowBalance, threshold: 100m, accountId: account1.Id),
            NewAlertRule(user2Id, AlertRuleType.LowBalance, threshold: 100m, accountId: account2.Id));
        db.DisableThrow = true;
        await db.SaveChangesAsync();
        db.DisableThrow = false;

        // Must not throw -- user1's simulated save failure is caught and logged, never allowed to
        // abort the batch, mirroring NetWorthSnapshotJob's T-06-06 precedent. User2's notification
        // must still be created.
        await CreateJob(db).ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, await db.Notifications.CountAsync(n => n.UserId == user1Id));
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == user2Id));
    }

    // ---- Test 7: NewRecurring (new) ----

    [Fact]
    public async Task ExecuteAsync_NewlyDetectedRecurring_FiresOnce_NeverAgainOnRerun()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        db.RecurringTransactions.Add(NewRecurringTransaction(
            userId, isNewlyDetected: true, amountIncreaseDetected: false, averageAmount: 50m));
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.NewRecurring));
        await db.SaveChangesAsync();

        var job = CreateJob(db);
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == userId));

        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == userId));
    }

    // ---- Test 8: NewRecurring (increase) ----

    [Fact]
    public async Task ExecuteAsync_AmountIncreaseDetectedRecurring_FiresDistinctNotification_FromNewFlag()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        db.RecurringTransactions.Add(NewRecurringTransaction(
            userId, isNewlyDetected: false, amountIncreaseDetected: true, averageAmount: 150m, previousAverageAmount: 100m));
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.NewRecurring));
        await db.SaveChangesAsync();

        await CreateJob(db).ExecuteAsync(CancellationToken.None);

        var notification = Assert.Single(await db.Notifications.Where(n => n.UserId == userId).ToListAsync());
        Assert.Equal(AlertRuleType.NewRecurring, notification.Type);
        Assert.Contains("increase", notification.Title, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Test 9: SyncError ----

    [Fact]
    public async Task ExecuteAsync_ConnectionInLoginError_FiresSyncErrorNotification()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        db.Connections.Add(NewConnection(userId, ConnectionStatus.LoginError));
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.SyncError));
        await db.SaveChangesAsync();

        await CreateJob(db).ExecuteAsync(CancellationToken.None);

        var notification = Assert.Single(await db.Notifications.Where(n => n.UserId == userId).ToListAsync());
        Assert.Equal(AlertRuleType.SyncError, notification.Type);
    }

    [Fact]
    public async Task ExecuteAsync_ConnectionHealthy_NoSyncErrorNotification()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        db.Connections.Add(NewConnection(userId, ConnectionStatus.Updated));
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.SyncError));
        await db.SaveChangesAsync();

        await CreateJob(db).ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, await db.Notifications.CountAsync(n => n.UserId == userId));
    }

    // ---- Test 10: GoalReached ----

    [Fact]
    public async Task ExecuteAsync_UnlinkedGoalAtTarget_FiresGoalReachedNotification_AndFlipsStatusToCompleted_NeverAgainOnRerun()
    {
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        user.Id = userId;
        db.Users.Add(user);
        var goal = NewGoal(userId, GoalStatus.Active, targetAmount: 1000m, currentAmount: 1000m);
        db.Goals.Add(goal);
        db.AlertRules.Add(NewAlertRule(userId, AlertRuleType.GoalReached));
        await db.SaveChangesAsync();

        var job = CreateJob(db);
        await job.ExecuteAsync(CancellationToken.None);

        var notification = Assert.Single(await db.Notifications.Where(n => n.UserId == userId).ToListAsync());
        Assert.Equal(AlertRuleType.GoalReached, notification.Type);

        var reloadedGoal = await db.Goals.SingleAsync(g => g.Id == goal.Id);
        Assert.Equal(GoalStatus.Completed, reloadedGoal.Status);

        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == userId));
    }

    // ---- Test 11: Hangfire/DI wiring smoke test ----

    [Fact]
    public void DiContainer_ResolvesAlertEvaluationJob_WithoutThrowing()
    {
        var services = new ServiceCollection();
        services.AddDbContext<SimpleFinanceDbContext>(o => o.UseNpgsql(GetConnectionString(), vec => vec.UseVector()));
        services.AddSingleton<IDbConnectionFactory>(_ =>
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = GetConnectionString(),
                })
                .Build();
            return new NpgsqlDbConnectionFactory(configuration);
        });
        services.AddSingleton<DashboardQueries>();
        services.AddScoped<BudgetService>();
        services.AddScoped<GoalService>();
        services.Configure<NotificationOptions>(_ => { });
        services.AddScoped<INotificationChannel, TelegramChannel>();
        services.AddScoped<INotificationChannel, EmailChannel>();
        services.AddScoped<NotificationDispatcher>();
        services.Configure<AlertEvaluationOptions>(_ => { });
        services.AddScoped<AlertEvaluationJob>();
        services.AddLogging();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var job = scope.ServiceProvider.GetRequiredService<AlertEvaluationJob>();

        Assert.NotNull(job);
    }

    /// <summary>
    /// Test-only DbContext subclass that throws on SaveChangesAsync whenever a tracked
    /// AlertFireLog or Notification for a specific UserId is about to be persisted -- simulates a
    /// malformed-rule-triggered mid-evaluation failure for that one user, exercising
    /// AlertEvaluationJob's per-user try/catch isolation against a real database for every other
    /// user in the same batch. Mirrors NetWorthSnapshotJobTests.ThrowOnSaveForUserDbContext.
    /// </summary>
    private sealed class ThrowOnSaveForUserDbContext(
        DbContextOptions<SimpleFinanceDbContext> options, Guid throwForUserId)
        : SimpleFinanceDbContext(options)
    {
        private readonly Guid _throwForUserId = throwForUserId;

        public bool DisableThrow { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!DisableThrow &&
                (ChangeTracker.Entries<AlertFireLog>().Any(e => e.Entity.UserId == _throwForUserId
                    && (e.State == EntityState.Added || e.State == EntityState.Modified))
                || ChangeTracker.Entries<Notification>().Any(e => e.Entity.UserId == _throwForUserId
                    && (e.State == EntityState.Added || e.State == EntityState.Modified))))
            {
                throw new InvalidOperationException("simulated per-user alert evaluation save failure");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
