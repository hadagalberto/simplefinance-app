namespace SimpleFinance.Domain.Common;

/// <summary>
/// Base class every operational (per-user) entity inherits: RefreshToken, RecoveryCode, and
/// everything from Phase 2 onward (Connection, Account, Transaction, etc.). Adds a UserId
/// column (single-user today, multi-user-ready per FND-04) and soft-delete support.
/// Inheriting this class is the entire mechanism for satisfying FND-04 — no per-entity
/// discipline required.
/// </summary>
public abstract class UserOwnedEntity : AuditableEntity, ISoftDelete
{
    public Guid UserId { get; set; }

    public DateTime? DeletedAt { get; set; }
}
