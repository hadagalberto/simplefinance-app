namespace SimpleFinance.Application.Categories.Update;

/// <summary>
/// CAT-05: edits an existing category's name/type/icon/color/parent. Allowed on ANY category
/// including system ones -- PRD RF-CAT-005 says "editar", edit is never delete-protected, only
/// deletion is (see DeleteCategoryAsync's IsSystem check in CategoriesEndpoints.cs).
/// </summary>
public sealed record UpdateCategoryCommand(
    string Name,
    string Type,
    string? Icon,
    string? Color,
    Guid? ParentCategoryId);
