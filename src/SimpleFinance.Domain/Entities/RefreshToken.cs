using SimpleFinance.Domain.Common;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// An opaque rotating refresh token. Only the SHA-256 hash of the issued plaintext token is
/// ever persisted (TokenHash) — the plaintext is returned to the caller once, at issuance, and
/// never stored. ReplacedByTokenId and FamilyId together implement rotation + reuse-detection
/// (see IRefreshTokenService / RefreshTokenService): FamilyId is set to the first token's own
/// Id at initial issuance and copied forward on every rotation, so revoking an entire rotation
/// family on reuse detection is a single-column bulk update.
/// </summary>
public class RefreshToken : UserOwnedEntity
{
    /// <summary>SHA-256 hex digest (64 chars) of the plaintext opaque token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Self-referencing FK to the token this row was rotated into. Set when this token is
    /// rotated (its replacement's Id); null while still the active/unrotated token.
    /// </summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>
    /// Shared identifier for an entire rotation chain, set to the first token's own Id at
    /// initial issuance and copied forward on every rotation. Used to cascade-revoke the whole
    /// family when a rotated-but-reused token is detected.
    /// </summary>
    public Guid FamilyId { get; set; }
}
