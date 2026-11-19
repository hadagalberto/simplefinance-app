namespace SimpleFinance.Domain.Common;

/// <summary>
/// Base class for entities that are not per-user (e.g. the single <c>User</c> entity itself).
/// Provides identity and creation/update auditing. All timestamps are UTC — enforced globally
/// by SimpleFinanceDbContext's ConfigureConventions via UtcDateTimeValueConverter.
/// </summary>
public abstract class AuditableEntity
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
