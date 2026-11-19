using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Auth;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Security;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Runtime key-resolution contract for <see cref="AiKeyProvider"/> (the DB-decrypt-else-env-trim path
/// the singleton bearer handlers resolve per request). Uses EF InMemory + the REAL
/// <see cref="AesTotpSecretProtector"/> so the encrypt-on-store / decrypt-on-read round-trip is
/// exercised end to end. Covers: DB key preferred over env, whitespace trimmed on both sides, env
/// fallback when the DB column is null, and null when neither is set.
/// </summary>
public class AiKeyProviderTests
{
    // A valid Base64 32-byte AES-256 key (same shape the tests/host use for Totp:EncryptionKey).
    private const string EncryptionKey = "IZS7xtbC5Uj4lmJzMPDBA7PWgzb5irgqIKSB9jnZnns=";

    private static ITotpSecretProtector Protector() =>
        new AesTotpSecretProtector(Options.Create(new TotpOptions { EncryptionKey = EncryptionKey }));

    private static IOptions<AiOptions> AiOpts(string deepSeekEnv = "", string openAiEnv = "") =>
        Options.Create(new AiOptions { DeepSeekApiKey = deepSeekEnv, OpenAiApiKey = openAiEnv });

    private static SimpleFinanceDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static Guid SeedSettings(
        SimpleFinanceDbContext db,
        ITotpSecretProtector protector,
        string? deepSeekPlain = null,
        string? openAiPlain = null)
    {
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        db.Users.Add(new User { Id = userId, Name = "Owner", Email = $"o-{userId:N}@x.com", PasswordHash = "x", CreatedAt = now, UpdatedAt = now });
        db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
            DeepSeekApiKeyEncrypted = deepSeekPlain is null ? null : protector.Encrypt(deepSeekPlain),
            OpenAiApiKeyEncrypted = openAiPlain is null ? null : protector.Encrypt(openAiPlain),
        });
        db.SaveChanges();
        return userId;
    }

    [Fact]
    public async Task PrefersDecryptedDbKey_OverEnv()
    {
        var protector = Protector();
        await using var db = CreateDb();
        SeedSettings(db, protector, deepSeekPlain: "sk-db-deepseek", openAiPlain: "sk-db-openai");

        var provider = new AiKeyProvider(db, protector, AiOpts(deepSeekEnv: "sk-env-deepseek", openAiEnv: "sk-env-openai"));

        Assert.Equal("sk-db-deepseek", await provider.GetDeepSeekKeyAsync(CancellationToken.None));
        Assert.Equal("sk-db-openai", await provider.GetOpenAiKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TrimsWhitespace_OnDbKey()
    {
        var protector = Protector();
        await using var db = CreateDb();
        // A key pasted with a trailing newline — the production whitespace-401 case.
        SeedSettings(db, protector, deepSeekPlain: "  sk-padded-key\n");

        var provider = new AiKeyProvider(db, protector, AiOpts());

        Assert.Equal("sk-padded-key", await provider.GetDeepSeekKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FallsBackToEnv_WhenDbColumnNull_AndTrimsEnv()
    {
        var protector = Protector();
        await using var db = CreateDb();
        SeedSettings(db, protector, deepSeekPlain: null, openAiPlain: null);

        var provider = new AiKeyProvider(db, protector, AiOpts(deepSeekEnv: "  sk-env-deepseek  ", openAiEnv: "sk-env-openai"));

        Assert.Equal("sk-env-deepseek", await provider.GetDeepSeekKeyAsync(CancellationToken.None));
        Assert.Equal("sk-env-openai", await provider.GetOpenAiKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReturnsNull_WhenNeitherDbNorEnvSet()
    {
        var protector = Protector();
        await using var db = CreateDb();
        SeedSettings(db, protector, deepSeekPlain: null, openAiPlain: null);

        var provider = new AiKeyProvider(db, protector, AiOpts(deepSeekEnv: "", openAiEnv: "   "));

        Assert.Null(await provider.GetDeepSeekKeyAsync(CancellationToken.None));
        Assert.Null(await provider.GetOpenAiKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReturnsNull_WhenNoSettingsRow_AndNoEnv()
    {
        var protector = Protector();
        await using var db = CreateDb();
        // No UserSettings row at all.

        var provider = new AiKeyProvider(db, protector, AiOpts());

        Assert.Null(await provider.GetDeepSeekKeyAsync(CancellationToken.None));
        Assert.Null(await provider.GetOpenAiKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EncryptedColumn_IsCiphertext_NotPlaintext()
    {
        var protector = Protector();
        await using var db = CreateDb();
        var userId = SeedSettings(db, protector, deepSeekPlain: "sk-secret-plaintext");

        var row = await db.UserSettings.FirstAsync(s => s.UserId == userId);
        Assert.NotNull(row.DeepSeekApiKeyEncrypted);
        Assert.DoesNotContain("sk-secret-plaintext", row.DeepSeekApiKeyEncrypted);
        // Round-trips back to the original plaintext.
        Assert.Equal("sk-secret-plaintext", protector.Decrypt(row.DeepSeekApiKeyEncrypted!));
    }

    [Fact]
    public async Task AnthropicAndOpenRouter_PreferDbKey_FallBackToEnv_Trimmed()
    {
        var protector = Protector();
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        db.Users.Add(new User { Id = userId, Name = "Owner", Email = $"o-{userId:N}", PasswordHash = "x", CreatedAt = now, UpdatedAt = now });
        db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
            AnthropicApiKeyEncrypted = protector.Encrypt(" sk-db-anthropic\n"),
        });
        db.SaveChanges();

        var provider = new AiKeyProvider(db, protector, Options.Create(new AiOptions
        {
            AnthropicApiKey = "sk-env-anthropic",
            OpenRouterApiKey = "  sk-env-openrouter  ",
        }));

        Assert.Equal("sk-db-anthropic", await provider.GetKeyAsync(AiProvider.Anthropic, CancellationToken.None));
        Assert.Equal("sk-env-openrouter", await provider.GetKeyAsync(AiProvider.OpenRouter, CancellationToken.None));
        Assert.Null(await provider.GetKeyAsync(AiProvider.DeepSeek, CancellationToken.None));
    }
}
