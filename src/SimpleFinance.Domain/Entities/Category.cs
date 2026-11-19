using SimpleFinance.Domain.Common;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Domain.Entities;

/// <summary>
/// A spending/income category, per PRD §11.5. Supports SYNC-06's categorization call site.
/// This table intentionally starts EMPTY in Phase 2 — CAT-01's full seeded tree is Phase 4's
/// job (ROADMAP dependency ordering); only the schema is a Phase 2 deliverable.
/// </summary>
public class Category : UserOwnedEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Self-referencing FK for subcategories. No navigation property required.</summary>
    public Guid? ParentCategoryId { get; set; }

    public CategoryType Type { get; set; }

    public string? Icon { get; set; }

    public string? Color { get; set; }

    public bool IsSystem { get; set; }

    public bool IsHidden { get; set; }
}
