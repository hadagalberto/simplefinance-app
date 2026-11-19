using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Auth;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Security;

namespace SimpleFinance.UnitTests.Security;

public class RecoveryCodeServiceTests
{
    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static string Sha256Hex(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

    [Fact]
    public async Task GenerateAsync_Produces8DistinctPlaintextCodes_PersistsOnlyTheirHashes()
    {
        await using var db = CreateDbContext();
        var sut = new RecoveryCodeService(db);
        var userId = Guid.NewGuid();

        var codes = await sut.GenerateAsync(userId);

        Assert.Equal(8, codes.Count);
        Assert.Equal(8, codes.Distinct().Count());

        var rows = await db.RecoveryCodes.Where(rc => rc.UserId == userId).ToListAsync();
        Assert.Equal(8, rows.Count);
        Assert.All(rows, r => Assert.Null(r.UsedAt));

        foreach (var code in codes)
        {
            Assert.Contains(rows, r => r.CodeHash == Sha256Hex(code));
            // The plaintext code itself must never appear as a stored hash value.
            Assert.DoesNotContain(rows, r => r.CodeHash == code);
        }
    }

    [Fact]
    public async Task ConsumeAsync_CorrectCode_SucceedsOnce_ThenSecondAttemptReturnsAlreadyUsed()
    {
        await using var db = CreateDbContext();
        var sut = new RecoveryCodeService(db);
        var userId = Guid.NewGuid();
        var codes = await sut.GenerateAsync(userId);
        var code = codes[0];

        var first = await sut.ConsumeAsync(userId, code);
        Assert.Equal(RecoveryCodeConsumeOutcome.Success, first.Outcome);

        var row = await db.RecoveryCodes.SingleAsync(rc => rc.CodeHash == Sha256Hex(code));
        Assert.NotNull(row.UsedAt);

        var second = await sut.ConsumeAsync(userId, code);
        Assert.Equal(RecoveryCodeConsumeOutcome.AlreadyUsed, second.Outcome);
    }

    [Fact]
    public async Task ConsumeAsync_CodeThatWasNeverIssued_ReturnsNotFound_DistinctFromAlreadyUsed()
    {
        await using var db = CreateDbContext();
        var sut = new RecoveryCodeService(db);
        var userId = Guid.NewGuid();
        await sut.GenerateAsync(userId);

        var result = await sut.ConsumeAsync(userId, "ZZZZ-ZZZZ-ZZ");

        Assert.Equal(RecoveryCodeConsumeOutcome.NotFound, result.Outcome);
    }
}
