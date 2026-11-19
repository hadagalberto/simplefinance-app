using SimpleFinance.Domain.Common;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// The single application user. Inherits AuditableEntity directly (not UserOwnedEntity) — this
/// entity IS the identity anchor everything else (RefreshToken, RecoveryCode, and all future
/// operational entities) points back to via UserId, so it has no UserId of its own.
/// </summary>
public class User : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Argon2id-encoded hash string (algorithm tag + params + salt + hash), never a raw digest.
    /// Produced by Argon2idPasswordHasher.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// TOTP secret, encrypted at rest. Nullable — populated only once 2FA is enabled (01-05).
    /// Declared here now so 01-05 doesn't require a destructive migration.
    /// </summary>
    public string? TotpSecret { get; set; }

    public bool TotpEnabled { get; set; }

    public DateTime? LastLoginAt { get; set; }
}
