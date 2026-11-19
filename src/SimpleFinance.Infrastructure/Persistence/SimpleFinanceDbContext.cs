using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Persistence.Conventions;

namespace SimpleFinance.Infrastructure.Persistence;

/// <summary>
/// Application DbContext. Establishes, for every future entity, without any
/// per-entity Fluent API call:
///   - FND-01: decimal(18,2) precision on every decimal property.
///   - FND-02: UTC-only DateTime persistence/read-back via UtcDateTimeValueConverter.
///   - FND-04: a soft-delete query filter on every entity implementing ISoftDelete.
/// </summary>
public class SimpleFinanceDbContext : DbContext
{
    public SimpleFinanceDbContext(DbContextOptions<SimpleFinanceDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();

    public DbSet<Connection> Connections => Set<Connection>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<CreditCardDetail> CreditCardDetails => Set<CreditCardDetail>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<Investment> Investments => Set<Investment>();

    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<CategoryMapping> CategoryMappings => Set<CategoryMapping>();

    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();

    public DbSet<Budget> Budgets => Set<Budget>();

    public DbSet<NetWorthSnapshot> NetWorthSnapshots => Set<NetWorthSnapshot>();

    public DbSet<RecurringTransaction> RecurringTransactions => Set<RecurringTransaction>();

    public DbSet<Goal> Goals => Set<Goal>();

    public DbSet<AlertRule> AlertRules => Set<AlertRule>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<AlertFireLog> AlertFireLogs => Set<AlertFireLog>();

    public DbSet<UserSettings> UserSettings => Set<UserSettings>();

    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();

    public DbSet<TransactionEmbedding> TransactionEmbeddings => Set<TransactionEmbedding>();

    public DbSet<AiSuggestion> AiSuggestions => Set<AiSuggestion>();

    public DbSet<AiInsight> AiInsights => Set<AiInsight>();

    public DbSet<Forecast> Forecasts => Set<Forecast>();

    public DbSet<AiConversation> AiConversations => Set<AiConversation>();

    public DbSet<AiMessage> AiMessages => Set<AiMessage>();

    public DbSet<AiPendingAction> AiPendingActions => Set<AiPendingAction>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // FND-01 (PITFALLS.md #7): global decimal(18,2) precision — never rely on EF Core's
        // default convention, never float/double in the money path. Any field genuinely
        // needing a different scale (e.g. a future investment rate) must override explicitly
        // via HasPrecision on that specific property.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);

        // FND-02 (PITFALLS.md #8): every DateTime is written/read as UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeValueConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // AI-FND-03 (15-02): declare the pgvector extension so EF tracks it and the
        // EnablePgvector migration emits CREATE EXTENSION. NO vector column/table this
        // phase — Phase 16 adds TransactionEmbedding with Vector(1536).
        modelBuilder.HasPostgresExtension("vector");

        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SimpleFinanceDbContext).Assembly);

        // AI-CAT-01: TransactionEmbedding.Embedding is a pgvector `vector(1536)` column, which only
        // the Npgsql provider (with UseVector()) can map. The InMemory provider used by the endpoint
        // /service unit tests has no mapping for the Vector CLR type, so keeping this entity in the
        // model would fail model validation for every InMemory-based test. Exclude it under non-Npgsql
        // providers — those tests never touch embeddings; runtime + Postgres integration tests keep it.
        if (!Database.IsNpgsql())
        {
            modelBuilder.Ignore<TransactionEmbedding>();
        }

        // FND-04: apply a global soft-delete query filter to every entity type that
        // implements ISoftDelete, generically via reflection — future entities inherit this
        // for free simply by inheriting UserOwnedEntity, no per-entity Fluent API needed.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ISoftDelete).IsAssignableFrom(entityType.ClrType))
            {
                var method = SetSoftDeleteQueryFilterMethod.MakeGenericMethod(entityType.ClrType);
                method.Invoke(null, new object[] { modelBuilder });
            }
        }
    }

    private static readonly MethodInfo SetSoftDeleteQueryFilterMethod =
        typeof(SimpleFinanceDbContext).GetMethod(
            nameof(SetSoftDeleteQueryFilter),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void SetSoftDeleteQueryFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ISoftDelete
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.DeletedAt == null);
    }
}
