namespace SimpleFinance.Domain.Common;

/// <summary>
/// Marker interface for entities that support soft-delete semantics.
/// Any entity implementing this interface automatically receives a
/// global query filter (see SimpleFinanceDbContext.OnModelCreating)
/// that excludes rows where <see cref="DeletedAt"/> is set.
/// </summary>
public interface ISoftDelete
{
    /// <summary>
    /// UTC timestamp the entity was soft-deleted. Null means the entity is active.
    /// </summary>
    DateTime? DeletedAt { get; set; }
}
