using SimpleFinance.Domain.Common;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// Maps a Pluggy-provided category to our own Category, per PRD §11.5 (no UserId). This is
/// SYNC-06's mapping-only stub data source. This table also starts EMPTY in Phase 2 — 02-04's
/// categorization call site must handle zero rows gracefully (return null CategoryId, never
/// throw), since Phase 4 owns seeding actual mappings.
/// </summary>
public class CategoryMapping : AuditableEntity
{
    /// <summary>Unique — Pluggy's own category identifier.</summary>
    public string PluggyCategoryId { get; set; } = string.Empty;

    public string? PluggyCategoryName { get; set; }

    public Guid CategoryId { get; set; }
}
