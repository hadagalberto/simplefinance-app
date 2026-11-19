using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.OpenAi;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.UnitTests.Ai.Fakes;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Postgres-backed integration tests for <see cref="AiEmbeddingJob"/> (16-02, AI-CAT-01). The job
/// writes a real pgvector <c>vector(1536)</c> column, which only the Npgsql provider (with
/// <c>UseVector()</c>) can map — EF Core's InMemory provider has no mapping for the
/// <c>Pgvector.Vector</c> CLR type, so these tests run against the docker-compose Postgres at
/// 127.0.0.1:5433 (same connection-string-resolution pattern as NetWorthSnapshotJobTests). Requires
/// <c>docker compose up postgres -d</c> (the pgvector/pgvector:pg17 image) running locally.
///
/// Every test scopes its rows to a fresh Guid user, so tests share the database without interfering.
/// The only users the job ever processes are those with AI + categorization enabled; no other test
/// seeds such a UserSettings row, so cross-test contamination of a specific user's embeddings is not
/// possible — assertions are always scoped to the test's own UserId.
/// </summary>
public class AiEmbeddingJobTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static IOptions<AiOptions> Options() => Microsoft.Extensions.Options.Options.Create(new AiOptions
    {
        OpenAiEmbeddingModel = "text-embedding-3-small",
    });

    private static AiEmbeddingJob CreateJob(SimpleFinanceDbContext db, FakeEmbeddingClient embeddingClient) =>
        new(db, embeddingClient, new PiiRedactor(), Options(), NullLogger<AiEmbeddingJob>.Instance);

    private static Guid SeedEnabledUser(SimpleFinanceDbContext db, bool aiEnabled = true, bool categorizationEnabled = true)
    {
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Name = "Owner",
            Email = $"owner-{userId:N}@example.com",
            PasswordHash = "x",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AiEnabled = aiEnabled,
            AiCategorizationEnabled = categorizationEnabled,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
        return userId;
    }

    private static Transaction NewTransaction(Guid userId, string description, string? merchantName)
    {
        var now = DateTime.UtcNow;
        return new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = Guid.NewGuid(),
            PluggyTransactionId = $"ptx-{Guid.NewGuid():N}",
            Date = now,
            Description = description,
            MerchantName = merchantName,
            Amount = -50m,
            Type = TransactionType.Debit,
            CurrencyCode = "BRL",
            Status = TransactionStatus.Posted,
            CategorySource = CategorySource.SystemMapping,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>A per-test unique token made only of letters g..v — never matched by PiiRedactor's
    /// token/CPF/CNPJ/digit-run masks, so the redacted text stays distinct across tests sharing the
    /// same Postgres database.</summary>
    private static string UniqueTag() =>
        new(Guid.NewGuid().ToString("N")
            .Select(c => (char)('g' + Convert.ToInt32(c.ToString(), 16)))
            .Take(12)
            .ToArray());

    [Fact]
    public async Task Execute_EmbedsTransactionsWithNoEmbedding_CreatesOneVectorRowEach()
    {
        await using var db = CreateDbContext();
        var userId = SeedEnabledUser(db);
        var tag = UniqueTag();
        db.Transactions.AddRange(
            NewTransaction(userId, "Compra A", $"Loja A {tag}"),
            NewTransaction(userId, "Compra B", $"Loja B {tag}"),
            NewTransaction(userId, "Compra C", $"Loja C {tag}"));
        await db.SaveChangesAsync();

        var fake = new FakeEmbeddingClient();
        await CreateJob(db, fake).ExecuteAsync(CancellationToken.None);

        var rows = await db.TransactionEmbeddings.Where(te => te.UserId == userId).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("text-embedding-3-small", r.EmbeddingModel);
            Assert.Equal(FakeEmbeddingClient.Dimensions, r.Embedding.ToArray().Length);
            Assert.False(string.IsNullOrEmpty(r.SourceTextHash));
        });
    }

    [Fact]
    public async Task Execute_TwoTransactionsSameNormalizedText_EmbedsUniqueOnce_BothRowsShareHash()
    {
        await using var db = CreateDbContext();
        var userId = SeedEnabledUser(db);
        var tag = UniqueTag();
        // Same "Description + MerchantName" up to internal whitespace → identical normalized hash.
        db.Transactions.AddRange(
            NewTransaction(userId, "Mercado", $"Pago {tag}"),
            NewTransaction(userId, "Mercado ", $" Pago  {tag}"));
        await db.SaveChangesAsync();

        var sharedHash = OpenAiEmbeddingClient.HashNormalized(new PiiRedactor().Redact($"Mercado Pago {tag}"));

        var fake = new FakeEmbeddingClient();
        await CreateJob(db, fake).ExecuteAsync(CancellationToken.None);

        var rows = await db.TransactionEmbeddings.Where(te => te.UserId == userId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(sharedHash, r.SourceTextHash));
        // Both rows carry the same vector (the duplicate was copied, not re-embedded).
        Assert.Equal(rows[0].Embedding.ToArray(), rows[1].Embedding.ToArray());
        // The shared text was sent to the embedding client exactly once for this pair.
        Assert.Equal(1, fake.ReceivedTexts.Count(t => OpenAiEmbeddingClient.HashNormalized(t) == sharedHash));
    }

    [Fact]
    public async Task Execute_HashAlreadyEmbedded_CopiesVector_WithoutReCallingEmbed()
    {
        await using var db = CreateDbContext();
        var userId = SeedEnabledUser(db);
        var tag = UniqueTag();
        var redactor = new PiiRedactor();
        var text = redactor.Redact($"Assinatura Netflix {tag}");
        var hash = OpenAiEmbeddingClient.HashNormalized(text);

        // Pre-existing embedding row (already vectorized in a prior run) with a recognizable vector.
        var seededVector = new float[FakeEmbeddingClient.Dimensions];
        seededVector[0] = 0.42f;
        var existingTxn = NewTransaction(userId, "Assinatura", $"Netflix {tag}");
        db.Transactions.Add(existingTxn);
        db.TransactionEmbeddings.Add(new TransactionEmbedding
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TransactionId = existingTxn.Id,
            Embedding = new Pgvector.Vector(seededVector),
            EmbeddingModel = "text-embedding-3-small",
            SourceTextHash = hash,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        // A NEW transaction whose text normalizes to the same hash.
        var newTxn = NewTransaction(userId, "Assinatura", $"Netflix {tag}");
        db.Transactions.Add(newTxn);
        await db.SaveChangesAsync();

        var fake = new FakeEmbeddingClient();
        await CreateJob(db, fake).ExecuteAsync(CancellationToken.None);

        var newRow = await db.TransactionEmbeddings.SingleAsync(te => te.TransactionId == newTxn.Id);
        Assert.Equal(hash, newRow.SourceTextHash);
        Assert.Equal(seededVector, newRow.Embedding.ToArray()); // copied from the cached vector
        // No embed input carried this hash — it was served entirely from the persistent cache.
        Assert.DoesNotContain(fake.ReceivedTexts, t => OpenAiEmbeddingClient.HashNormalized(t) == hash);
    }

    [Fact]
    public async Task Execute_WhenBudgetExceeded_StopsGracefully_PersistsCacheHits_LeavesRestForNextRun()
    {
        await using var db = CreateDbContext();
        var userId = SeedEnabledUser(db);
        var tag = UniqueTag();
        var redactor = new PiiRedactor();
        var cachedText = redactor.Redact($"Padaria {tag}");
        var cachedHash = OpenAiEmbeddingClient.HashNormalized(cachedText);

        // A pre-existing embedding so one new txn is a pure cache-copy (no embed needed) ...
        var priorTxn = NewTransaction(userId, "Padaria", $"{tag}");
        db.Transactions.Add(priorTxn);
        db.TransactionEmbeddings.Add(new TransactionEmbedding
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TransactionId = priorTxn.Id,
            Embedding = new Pgvector.Vector(new float[FakeEmbeddingClient.Dimensions]),
            EmbeddingModel = "text-embedding-3-small",
            SourceTextHash = cachedHash,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var cacheHitTxn = NewTransaction(userId, "Padaria", $"{tag}");   // same hash → copied
        var needsEmbedTxn = NewTransaction(userId, "Farmacia", $"Nova {tag}"); // needs a real embed call
        db.Transactions.AddRange(cacheHitTxn, needsEmbedTxn);
        await db.SaveChangesAsync();

        var fake = new FakeEmbeddingClient { ThrowBudgetExceeded = true };
        var job = CreateJob(db, fake);

        // Must NOT throw — a budget cap is an expected degrade, not a failure.
        var ex = await Record.ExceptionAsync(() => job.ExecuteAsync(CancellationToken.None));
        Assert.Null(ex);

        // The cache-hit txn was persisted from cache (no embed call needed) ...
        Assert.True(await db.TransactionEmbeddings.AnyAsync(te => te.TransactionId == cacheHitTxn.Id));
        // ... while the txn that needed a real embed is left for the next run.
        Assert.False(await db.TransactionEmbeddings.AnyAsync(te => te.TransactionId == needsEmbedTxn.Id));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Execute_WhenAiOrCategorizationDisabled_DoesNothing(bool aiEnabled, bool categorizationEnabled)
    {
        await using var db = CreateDbContext();
        var userId = SeedEnabledUser(db, aiEnabled, categorizationEnabled);
        var tag = UniqueTag();
        var txn = NewTransaction(userId, "Compra", $"Loja {tag}");
        db.Transactions.Add(txn);
        await db.SaveChangesAsync();

        var fake = new FakeEmbeddingClient();
        await CreateJob(db, fake).ExecuteAsync(CancellationToken.None);

        Assert.False(await db.TransactionEmbeddings.AnyAsync(te => te.UserId == userId));
        Assert.DoesNotContain(fake.ReceivedTexts, t => t.Contains(tag));
    }
}
