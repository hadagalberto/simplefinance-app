using SimpleFinance.Domain.Common;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// Single-use 2FA recovery code. PRD §11.1 suggests a `RecoveryCodesHash` (json/list) column on
/// User; this deliberately uses a dedicated table instead — single-use consumption tracking
/// (which specific code was used, when) is materially simpler and more reliable as one row per
/// code (an atomic `UsedAt` UPDATE ... WHERE UsedAt IS NULL) than as in-place JSON array
/// mutation. See 01-05-PLAN.md's explicit deviation note.
/// </summary>
public class RecoveryCode : UserOwnedEntity
{
    /// <summary>SHA-256 hex digest of the plaintext recovery code. Plaintext is never persisted.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>Set the first (and only) time this code is successfully consumed.</summary>
    public DateTime? UsedAt { get; set; }
}
