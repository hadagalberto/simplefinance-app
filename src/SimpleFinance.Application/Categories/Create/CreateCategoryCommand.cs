namespace SimpleFinance.Application.Categories.Create;

/// <summary>
/// CAT-05: creates a new user-owned category (never a system category -- the handler always
/// sets IsSystem=false/IsHidden=false). <see cref="Type"/> is a string parsed against
/// CategoryType by CreateCategoryValidator (Api project, see CategoriesEndpoints.cs) so an
/// invalid value returns 400 instead of a raw model-binding failure.
/// </summary>
public sealed record CreateCategoryCommand(
    string Name,
    string Type,
    string? Icon,
    string? Color,
    Guid? ParentCategoryId);
