namespace SimpleFinance.Application.Auth;

/// <summary>Outcome of a refresh-token rotation attempt.</summary>
public enum RefreshRotationOutcome
{
    /// <summary>Rotation succeeded; a new token was issued and the presented one revoked.</summary>
    Success,

    /// <summary>The presented token does not match any known token hash.</summary>
    Invalid,

    /// <summary>The presented token was found but has expired.</summary>
    Expired,

    /// <summary>
    /// The presented token was already rotated once before (its replacement exists) and is now
    /// being presented again — a reuse/replay signal. The entire rotation family has been
    /// cascade-revoked as a result.
    /// </summary>
    ReusedAndFamilyRevoked,
}

/// <summary>
/// Result of <see cref="IRefreshTokenService.RotateAsync"/>, distinguishing every outcome so
/// callers (the login/refresh HTTP endpoint) can map each case to the right HTTP status without
/// inspecting exceptions.
/// </summary>
public sealed record RefreshRotationResult(
    RefreshRotationOutcome Outcome,
    string? NewPlaintext = null,
    DateTime? NewExpiresAt = null,
    Guid? UserId = null)
{
    public static RefreshRotationResult Succeeded(string newPlaintext, DateTime newExpiresAt, Guid userId) =>
        new(RefreshRotationOutcome.Success, newPlaintext, newExpiresAt, userId);

    public static readonly RefreshRotationResult Invalid = new(RefreshRotationOutcome.Invalid);

    public static readonly RefreshRotationResult Expired = new(RefreshRotationOutcome.Expired);

    public static readonly RefreshRotationResult ReusedAndFamilyRevoked =
        new(RefreshRotationOutcome.ReusedAndFamilyRevoked);
}

/// <summary>
/// Issues, rotates, and revokes opaque refresh tokens. Only the SHA-256 hash of the plaintext
/// token is ever persisted — the plaintext is returned once, at issuance/rotation, and never
/// stored. Rotation implements reuse-detection: presenting an already-rotated token cascades a
/// revoke across its entire rotation family (see RefreshRotationOutcome.ReusedAndFamilyRevoked).
/// </summary>
public interface IRefreshTokenService
{
    Task<(string Plaintext, DateTime ExpiresAt)> IssueAsync(Guid userId, CancellationToken ct = default);

    Task<RefreshRotationResult> RotateAsync(string presentedPlaintext, CancellationToken ct = default);

    /// <summary>
    /// Revokes a single presented token (e.g. on logout). Idempotent: revoking an unknown or
    /// already-revoked token is a no-op, never an error — callers (POST /api/auth/logout) treat
    /// logout as always succeeding regardless of whether a session was actually active.
    /// </summary>
    Task RevokeAsync(string presentedPlaintext, CancellationToken ct = default);

    /// <summary>Revokes every active refresh token for a user (e.g. on password change/logout-all).</summary>
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);
}
