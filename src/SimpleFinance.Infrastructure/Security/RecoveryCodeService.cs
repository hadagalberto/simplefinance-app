using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Auth;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Security;

/// <summary>
/// Generates and consumes single-use recovery codes. Codes are 10 human-typeable, unambiguous
/// characters (uppercase alphanumeric excluding 0/O/1/I/L, which are easy to confuse when
/// hand-typed from a "write these down" screen), grouped XXXX-XXXX-XX for readability. Only a
/// SHA-256 hash of each code is ever persisted — plaintext is returned once, at generation.
/// </summary>
public class RecoveryCodeService : IRecoveryCodeService
{
    private const int CodeCount = 8;
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly SimpleFinanceDbContext _db;

    public RecoveryCodeService(SimpleFinanceDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<string>> GenerateAsync(Guid userId, CancellationToken ct = default)
    {
        // Regenerating recovery codes invalidates any prior unused set — delete every existing
        // row for this user (used or not) before issuing a fresh batch.
        var existing = await _db.RecoveryCodes.Where(rc => rc.UserId == userId).ToListAsync(ct);
        _db.RecoveryCodes.RemoveRange(existing);

        var now = DateTime.UtcNow;
        var plaintextCodes = new List<string>(CodeCount);

        while (plaintextCodes.Count < CodeCount)
        {
            var code = GenerateCode();
            if (plaintextCodes.Contains(code))
            {
                continue; // astronomically unlikely collision within a single batch; guard anyway.
            }

            plaintextCodes.Add(code);
            _db.RecoveryCodes.Add(new RecoveryCode
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CodeHash = Sha256Hex(code),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);

        return plaintextCodes;
    }

    public async Task<RecoveryCodeConsumeResult> ConsumeAsync(Guid userId, string plaintextCode, CancellationToken ct = default)
    {
        var normalized = Normalize(plaintextCode);
        var hash = Sha256Hex(normalized);

        var row = await _db.RecoveryCodes
            .SingleOrDefaultAsync(rc => rc.UserId == userId && rc.CodeHash == hash, ct);

        if (row is null)
        {
            return RecoveryCodeConsumeResult.NotFound;
        }

        if (row.UsedAt is not null)
        {
            return RecoveryCodeConsumeResult.AlreadyUsed;
        }

        row.UsedAt = DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return RecoveryCodeConsumeResult.Success;
    }

    private static string Normalize(string input) => input.Trim().ToUpperInvariant();

    private static string GenerateCode()
    {
        Span<char> raw = stackalloc char[10];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"{new string(raw[..4])}-{new string(raw[4..8])}-{new string(raw[8..10])}";
    }

    private static string Sha256Hex(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
}
