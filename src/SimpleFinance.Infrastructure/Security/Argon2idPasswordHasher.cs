using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using SimpleFinance.Application.Auth;

namespace SimpleFinance.Infrastructure.Security;

/// <summary>
/// Argon2id password hasher (OWASP-recommended mode — resists both side-channel and
/// GPU-cracking attacks, unlike Argon2i/Argon2d individually).
///
/// Tuning defaults (DegreeOfParallelism=2, MemorySize=64MB, Iterations=3) are a sane starting
/// point for an unknown/modest single-user VPS, per research/STACK.md's explicit "validate
/// against target VPS RAM" caveat — revisit if the deploy target turns out to be memory
/// constrained (STACK.md's fallback: drop to ~19MB per OWASP's minimum, or switch to BCrypt).
///
/// The encoded hash string is self-describing ($argon2id$v=19$m=..,t=..,p=..$salt$hash) so
/// Verify can always re-derive the parameters actually used to produce a given hash, even if
/// these constants change in a future deploy — never assume Verify runs with the current
/// constants only.
/// </summary>
public class Argon2idPasswordHasher : IPasswordHasher
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int DegreeOfParallelism = 2;
    private const int MemorySizeKb = 64 * 1024; // 64 MB
    private const int Iterations = 3;
    private const int ArgonVersion = 19; // 0x13 — Argon2 v1.3

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = ComputeHash(password, salt, DegreeOfParallelism, MemorySizeKb, Iterations, HashSizeBytes);

        return Encode(DegreeOfParallelism, MemorySizeKb, Iterations, salt, hash);
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        try
        {
            if (!TryDecode(hash, out var parallelism, out var memoryKb, out var iterations, out var salt, out var expectedHash))
            {
                return false;
            }

            var actualHash = ComputeHash(password, salt, parallelism, memoryKb, iterations, expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (Exception)
        {
            // Defensive: a corrupted/unexpected DB value must never crash the login endpoint.
            return false;
        }
    }

    private static byte[] ComputeHash(
        string password,
        byte[] salt,
        int degreeOfParallelism,
        int memorySizeKb,
        int iterations,
        int hashSizeBytes)
    {
        using var argon2 = new Argon2id(System.Text.Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = degreeOfParallelism,
            MemorySize = memorySizeKb,
            Iterations = iterations,
        };

        return argon2.GetBytes(hashSizeBytes);
    }

    private static string Encode(int parallelism, int memoryKb, int iterations, byte[] salt, byte[] hash)
    {
        return $"$argon2id$v={ArgonVersion}$m={memoryKb},t={iterations},p={parallelism}$"
            + $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool TryDecode(
        string encoded,
        out int parallelism,
        out int memoryKb,
        out int iterations,
        out byte[] salt,
        out byte[] hash)
    {
        parallelism = 0;
        memoryKb = 0;
        iterations = 0;
        salt = [];
        hash = [];

        // Expected shape: $argon2id$v=19$m=65536,t=3,p=2$<base64 salt>$<base64 hash>
        var parts = encoded.Split('$', StringSplitOptions.None);
        if (parts.Length != 6)
        {
            return false;
        }

        // parts[0] is empty (leading '$'), parts[1] = "argon2id"
        if (!string.Equals(parts[1], "argon2id", StringComparison.Ordinal))
        {
            return false;
        }

        var paramsSegment = parts[3];
        var paramPairs = paramsSegment.Split(',');
        int? m = null, t = null, p = null;

        foreach (var pair in paramPairs)
        {
            var kv = pair.Split('=');
            if (kv.Length != 2)
            {
                return false;
            }

            if (!int.TryParse(kv[1], out var value))
            {
                return false;
            }

            switch (kv[0])
            {
                case "m":
                    m = value;
                    break;
                case "t":
                    t = value;
                    break;
                case "p":
                    p = value;
                    break;
                default:
                    return false;
            }
        }

        if (m is null || t is null || p is null)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[4]);
            hash = Convert.FromBase64String(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || hash.Length == 0)
        {
            return false;
        }

        memoryKb = m.Value;
        iterations = t.Value;
        parallelism = p.Value;

        return true;
    }
}
