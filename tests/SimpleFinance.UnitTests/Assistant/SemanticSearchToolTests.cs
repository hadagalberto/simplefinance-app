using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.Assistant.Tools;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.UnitTests.Ai.Fakes;

namespace SimpleFinance.UnitTests.Assistant;

/// <summary>
/// Behavior tests for the AI-CHAT-03 RAG tool <see cref="SemanticSearchTool"/> (17-03). The kNN path
/// needs the real pgvector <c>vector(1536)</c> column, which only the Npgsql provider can map (the
/// entity is Ignored off-Npgsql), so the ordering/isolation/redaction/cap tests run against the
/// docker-compose Postgres at 127.0.0.1:5433 — the same fixture pattern as <c>AiEmbeddingJobTests</c>.
/// A <see cref="FakeEmbeddingClient"/> supplies deterministic vectors so kNN ordering is assertable
/// without a real OpenAI key. The degradation test uses the InMemory provider (non-Npgsql) to prove the
/// tool returns <c>note:"indisponível"</c> rather than throwing.
///
/// Every test scopes its rows to a fresh Guid user, so tests share the database without interfering.
/// </summary>
public class SemanticSearchToolTests
{
    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5433;Database=simplefinance;Username=simplefinance;Password=changeme";

    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseNpgsql(GetConnectionString(), vec => vec.UseVector())
            .Options);

    private static SimpleFinanceDbContext CreateInMemoryDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>A stub key resolver so the tool's gate can be exercised without a DbContext/protector —
    /// the DB-decrypt-else-env resolution itself is covered by AiKeyProviderTests.</summary>
    private sealed class StubKeyProvider(string? openAiKey) : IAiKeyProvider
    {
        public Task<string?> GetDeepSeekKeyAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> GetOpenAiKeyAsync(CancellationToken ct) => Task.FromResult(openAiKey);
    }

    private static IAiKeyProvider KeyProvider(string? openAiKey = "test-key") => new StubKeyProvider(openAiKey);

    /// <summary>A per-test token made only of letters g..v — never matched by any PiiRedactor mask, so
    /// redacted text stays distinct across tests sharing the same Postgres database.</summary>
    private static string UniqueTag() =>
        new(Guid.NewGuid().ToString("N")
            .Select(c => (char)('g' + Convert.ToInt32(c.ToString(), 16)))
            .Take(12)
            .ToArray());

    private static Guid SeedUser(SimpleFinanceDbContext db)
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
        db.SaveChanges();
        return userId;
    }

    /// <summary>Seeds a transaction plus its embedding row, the vector deterministically derived from
    /// <paramref name="embeddingText"/> via the fake so kNN ordering is controllable.</summary>
    private static Guid SeedTransactionWithEmbedding(
        SimpleFinanceDbContext db, Guid userId, string description, string? merchant, string embeddingText, decimal amount = -50m)
    {
        var now = DateTime.UtcNow;
        var txnId = Guid.NewGuid();
        db.Transactions.Add(new Transaction
        {
            Id = txnId,
            UserId = userId,
            AccountId = Guid.NewGuid(),
            PluggyTransactionId = $"ptx-{Guid.NewGuid():N}",
            Date = now,
            Description = description,
            MerchantName = merchant,
            Amount = amount,
            Type = TransactionType.Debit,
            CurrencyCode = "BRL",
            Status = TransactionStatus.Posted,
            CategorySource = CategorySource.SystemMapping,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var fake = new FakeEmbeddingClient();
        var vector = fake.EmbedAsync(new[] { embeddingText }, CancellationToken.None).Result[0];
        db.TransactionEmbeddings.Add(new TransactionEmbedding
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TransactionId = txnId,
            Embedding = new Pgvector.Vector(vector),
            EmbeddingModel = "text-embedding-3-small",
            SourceTextHash = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
        return txnId;
    }

    [Fact]
    public async Task Search_ReturnsNearestFirst_OrderedByCosineDistance()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db);
        var tag = UniqueTag();
        var redactor = new PiiRedactor();

        // The query the model will send; the tool embeds redactor.Redact(query).
        var query = $"farmacia remedio {tag}";
        var queryEmbeddingText = redactor.Redact(query);

        // Target txn's stored embedding == the query's embedding → cosine distance 0 → must rank first.
        var targetId = SeedTransactionWithEmbedding(db, userId, $"Drogaria {tag}", $"Farmacia {tag}", queryEmbeddingText, amount: -123.45m);
        // Two unrelated txns with different embedding texts → uncorrelated vectors → far.
        SeedTransactionWithEmbedding(db, userId, $"Uber {tag}", $"Corrida {tag}", $"transporte carro {tag}");
        SeedTransactionWithEmbedding(db, userId, $"Padaria {tag}", $"Pao {tag}", $"comida manha {tag}");

        var tool = new SemanticSearchTool(db, new FakeEmbeddingClient(), redactor, KeyProvider());
        var json = await tool.ExecuteAsync($$"""{"text":"{{query}}","topK":3}""", userId, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var matches = doc.RootElement.GetProperty("matches");
        Assert.Equal(3, matches.GetArrayLength());
        // The nearest (identical-vector) transaction's redacted merchant is first.
        Assert.Equal(redactor.Redact($"Farmacia {tag}"), matches[0].GetProperty("merchant").GetString());
        Assert.Equal(-123.45m, matches[0].GetProperty("amount").GetDecimal()); // signed amount preserved (debit)
        Assert.False(doc.RootElement.TryGetProperty("note", out _)); // available path, no "indisponível"
    }

    [Fact]
    public async Task Search_ClampsTopK_NeverExceedsCap()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db);
        var tag = UniqueTag();

        for (var i = 0; i < 30; i++)
        {
            SeedTransactionWithEmbedding(db, userId, $"Compra {i} {tag}", $"Loja {i} {tag}", $"item {i} {tag}");
        }

        var tool = new SemanticSearchTool(db, new FakeEmbeddingClient(), new PiiRedactor(), KeyProvider());

        // topK above the cap is clamped to 25.
        var capped = await tool.ExecuteAsync($$"""{"text":"compra {{tag}}","topK":100}""", userId, CancellationToken.None);
        using (var doc = JsonDocument.Parse(capped))
        {
            Assert.Equal(25, doc.RootElement.GetProperty("matches").GetArrayLength());
        }

        // topK omitted defaults to 10.
        var defaulted = await tool.ExecuteAsync($$"""{"text":"compra {{tag}}"}""", userId, CancellationToken.None);
        using (var doc = JsonDocument.Parse(defaulted))
        {
            Assert.Equal(10, doc.RootElement.GetProperty("matches").GetArrayLength());
        }
    }

    [Fact]
    public async Task Search_ScopedToUser_NeverReturnsOtherUsersRows()
    {
        await using var db = CreateDbContext();
        var userA = SeedUser(db);
        var userB = SeedUser(db);
        var tag = UniqueTag();
        var redactor = new PiiRedactor();

        var query = $"assinatura streaming {tag}";
        var queryText = redactor.Redact(query);

        // User B owns a transaction whose embedding is IDENTICAL to user A's query — if scoping were
        // broken it would be the nearest hit. It must never surface for user A.
        SeedTransactionWithEmbedding(db, userB, $"SEGREDO-B {tag}", $"SEGREDO-B {tag}", queryText);
        // User A owns one unrelated transaction.
        SeedTransactionWithEmbedding(db, userA, $"Conta A {tag}", $"Conta A {tag}", $"outra coisa {tag}");

        var tool = new SemanticSearchTool(db, new FakeEmbeddingClient(), redactor, KeyProvider());
        var json = await tool.ExecuteAsync($$"""{"text":"{{query}}","topK":25}""", userA, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var matches = doc.RootElement.GetProperty("matches");
        foreach (var m in matches.EnumerateArray())
        {
            Assert.DoesNotContain("SEGREDO-B", m.GetProperty("description").GetString());
            Assert.DoesNotContain("SEGREDO-B", m.GetProperty("merchant").GetString());
        }
    }

    [Fact]
    public async Task Search_RedactsDescriptionAndMerchant_NoRawPii()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db);
        var tag = UniqueTag();
        var redactor = new PiiRedactor();

        var query = $"pagamento {tag}";
        var queryText = redactor.Redact(query);
        // A CPF embedded in the free text — must never re-enter the model context raw.
        SeedTransactionWithEmbedding(db, userId, $"Pix CPF 123.456.789-09 {tag}", $"Titular 123.456.789-09 {tag}", queryText);

        var tool = new SemanticSearchTool(db, new FakeEmbeddingClient(), redactor, KeyProvider());
        var json = await tool.ExecuteAsync($$"""{"text":"{{query}}","topK":5}""", userId, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.GetProperty("matches")[0];
        Assert.DoesNotContain("123.456.789-09", first.GetProperty("description").GetString());
        Assert.DoesNotContain("123.456.789-09", first.GetProperty("merchant").GetString());
        Assert.Contains("«REDACTED»", first.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Search_NonNpgsqlProvider_ReturnsIndisponivel_NoThrow()
    {
        await using var db = CreateInMemoryDbContext();
        var userId = Guid.NewGuid();

        var tool = new SemanticSearchTool(db, new FakeEmbeddingClient(), new PiiRedactor(), KeyProvider());
        var json = await tool.ExecuteAsync("""{"text":"qualquer coisa","topK":5}""", userId, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("indisponível", doc.RootElement.GetProperty("note").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public async Task Search_NoEmbeddingKey_ReturnsIndisponivel()
    {
        await using var db = CreateDbContext();
        var userId = SeedUser(db);

        // Npgsql provider, but no OpenAI key configured → nothing to embed → graceful "indisponível".
        var tool = new SemanticSearchTool(db, new FakeEmbeddingClient(), new PiiRedactor(), KeyProvider(openAiKey: ""));
        var json = await tool.ExecuteAsync("""{"text":"qualquer coisa","topK":5}""", userId, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("indisponível", doc.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Search_EmptyText_ReturnsError()
    {
        await using var db = CreateInMemoryDbContext();
        var tool = new SemanticSearchTool(db, new FakeEmbeddingClient(), new PiiRedactor(), KeyProvider());

        var json = await tool.ExecuteAsync("""{"text":"  "}""", Guid.NewGuid(), CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("invalid_query", doc.RootElement.GetProperty("error").GetString());
    }
}
