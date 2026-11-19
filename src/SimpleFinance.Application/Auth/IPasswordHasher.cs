namespace SimpleFinance.Application.Auth;

/// <summary>
/// Hashes and verifies passwords. Implementations must use a memory-hard algorithm
/// (Argon2id) with a per-call random salt — never a fixed or derived salt.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>
    /// Hashes <paramref name="password"/>, producing a self-describing string that encodes the
    /// algorithm tag, tuning parameters, salt, and derived hash — so <see cref="Verify"/> can
    /// re-derive the same parameters even if the hasher's default tuning constants change later.
    /// </summary>
    string Hash(string password);

    /// <summary>
    /// Verifies <paramref name="password"/> against a previously produced <paramref name="hash"/>.
    /// Must return false (never throw) for a malformed or empty hash string.
    /// </summary>
    bool Verify(string password, string hash);
}
