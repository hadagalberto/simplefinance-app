namespace SimpleFinance.Application.Auth;

/// <summary>Outcome of a recovery-code consumption attempt.</summary>
public enum RecoveryCodeConsumeOutcome
{
    /// <summary>The code matched an unused row; it is now marked used.</summary>
    Success,

    /// <summary>The code matched a row that was already consumed previously.</summary>
    AlreadyUsed,

    /// <summary>The code does not match any issued row for this user.</summary>
    NotFound,
}

/// <summary>
/// Result of <see cref="IRecoveryCodeService.ConsumeAsync"/>, distinguishing every outcome so
/// the /2fa/verify endpoint can map each case to the right, distinguishable error per this
/// plan's api_contract ("invalid_code" vs "recovery_code_already_used").
/// </summary>
public sealed record RecoveryCodeConsumeResult(RecoveryCodeConsumeOutcome Outcome)
{
    public static readonly RecoveryCodeConsumeResult Success = new(RecoveryCodeConsumeOutcome.Success);
    public static readonly RecoveryCodeConsumeResult AlreadyUsed = new(RecoveryCodeConsumeOutcome.AlreadyUsed);
    public static readonly RecoveryCodeConsumeResult NotFound = new(RecoveryCodeConsumeOutcome.NotFound);
}

/// <summary>
/// Generates and consumes single-use 2FA recovery codes. Only a SHA-256 hash of each code is
/// ever persisted — the plaintext list is returned once, at generation, and never stored.
/// </summary>
public interface IRecoveryCodeService
{
    /// <summary>
    /// Generates exactly 8 new single-use recovery codes for the user, deleting any prior unused
    /// codes first (regenerating invalidates the old set). Returns the plaintext codes — this is
    /// the only time they are ever available in plaintext.
    /// </summary>
    Task<IReadOnlyList<string>> GenerateAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Attempts to consume a plaintext recovery code for the given user.</summary>
    Task<RecoveryCodeConsumeResult> ConsumeAsync(Guid userId, string plaintextCode, CancellationToken ct = default);
}
