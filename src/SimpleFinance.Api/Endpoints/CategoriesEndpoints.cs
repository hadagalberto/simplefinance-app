using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Categories.Create;
using SimpleFinance.Application.Categories.ToggleHide;
using SimpleFinance.Application.Categories.Update;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// CAT-05: full category CRUD (create/edit/hide-unhide/delete) on top of the read-only
/// GET /api/categories group established in Phase 3. System categories (IsSystem=true, seeded
/// by 04-02's CategorySeeder) are editable and hideable like any other category -- only
/// deletion is blocked (403). Deleting a category still referenced by a Transaction/CategoryRule/
/// CategoryMapping returns 409 since no cascade delete is configured on any of those FKs.
/// Mirrors AccountsEndpoints.cs/TransactionsEndpoints.cs's exact structure (group +
/// RequireAuthorization + GetUserId helper + T-03-09 defense-in-depth UserId scoping on every
/// write).
/// </summary>
public static class CategoriesEndpoints
{
    public static void MapCategoriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/categories").RequireAuthorization();
        group.MapGet("", GetCategoriesAsync);
        group.MapPost("", CreateCategoryAsync);
        group.MapPut("/{id:guid}", UpdateCategoryAsync);
        group.MapPatch("/{id:guid}/hide", ToggleCategoryHideAsync);
        group.MapDelete("/{id:guid}", DeleteCategoryAsync);
    }

    private static async Task<IResult> GetCategoriesAsync(
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var categories = await db.Categories
            .Where(c => c.UserId == userId.Value)
            .Select(c => new CategoryResponse(
                c.Id,
                c.Name,
                c.ParentCategoryId,
                c.Type.ToString(),
                c.Icon,
                c.Color,
                c.IsHidden,
                c.IsSystem))
            .ToListAsync(ct);

        return Results.Ok(categories);
    }

    private static async Task<IResult> CreateCategoryAsync(
        CreateCategoryCommand command,
        IValidator<CreateCategoryCommand> validator,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        await ValidateWithUserAsync(validator, command, userId.Value, ct);

        var now = DateTime.UtcNow;
        var category = new Category
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            Name = command.Name,
            Type = Enum.Parse<CategoryType>(command.Type, ignoreCase: true),
            Icon = command.Icon,
            Color = command.Color,
            ParentCategoryId = command.ParentCategoryId,
            IsSystem = false,
            IsHidden = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/categories/{category.Id}", ToResponse(category));
    }

    private static async Task<IResult> UpdateCategoryAsync(
        Guid id,
        UpdateCategoryCommand command,
        IValidator<UpdateCategoryCommand> validator,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        await ValidateWithUserAsync(validator, command, userId.Value, ct);

        // T-03-01/T-03-09 precedent: explicit UserId scoping in addition to the soft-delete
        // query filter. Editing is allowed on ANY category the caller owns, including system
        // ones -- only DeleteCategoryAsync checks IsSystem.
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId.Value, ct);
        if (category is null)
        {
            return Results.NotFound();
        }

        category.Name = command.Name;
        category.Type = Enum.Parse<CategoryType>(command.Type, ignoreCase: true);
        category.Icon = command.Icon;
        category.Color = command.Color;
        category.ParentCategoryId = command.ParentCategoryId;
        category.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(category));
    }

    private static async Task<IResult> ToggleCategoryHideAsync(
        Guid id,
        ToggleCategoryHideCommand command,
        IValidator<ToggleCategoryHideCommand> validator,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(command, ct);

        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId.Value, ct);
        if (category is null)
        {
            return Results.NotFound();
        }

        // Hiding/unhiding is always allowed, even on system categories -- only DELETE is
        // system-protected.
        category.IsHidden = command.IsHidden!.Value;
        category.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(category));
    }

    private static async Task<IResult> DeleteCategoryAsync(
        Guid id,
        SimpleFinanceDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId.Value, ct);
        if (category is null)
        {
            return Results.NotFound();
        }

        // T-04-06: system categories are hideable, never deletable -- checked before any other
        // delete logic runs.
        if (category.IsSystem)
        {
            return Results.Json(
                new { error = "System categories cannot be deleted. Hide it instead." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        // T-04-07: no cascade delete is configured on Transaction.CategoryId,
        // CategoryRule.CategoryId, or CategoryMapping.CategoryId -- this pre-delete existence
        // check across all three referencing tables is the only backstop preventing orphaned FK
        // values.
        var isReferenced = await db.Transactions.AnyAsync(t => t.CategoryId == id, ct)
            || await db.CategoryRules.AnyAsync(r => r.CategoryId == id, ct)
            || await db.CategoryMappings.AnyAsync(m => m.CategoryId == id, ct);

        if (isReferenced)
        {
            return Results.Json(
                new { error = "Category is still in use -- reassign or hide it instead." },
                statusCode: StatusCodes.Status409Conflict);
        }

        db.Categories.Remove(category);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // Threads the authenticated caller's UserId into FluentValidation's RootContextData so a
    // DB-dependent MustAsync rule (CreateCategoryValidator/UpdateCategoryValidator) can scope its
    // parentCategoryId existence+ownership check without the command itself carrying UserId.
    // Mirrors TransactionsEndpoints.ValidateWithUserAsync<T> exactly (03-05-SUMMARY.md precedent).
    private static async Task ValidateWithUserAsync<T>(IValidator<T> validator, T command, Guid userId, CancellationToken ct)
    {
        var context = new ValidationContext<T>(command);
        context.RootContextData["UserId"] = userId;
        var result = await validator.ValidateAsync(context, ct);
        if (!result.IsValid)
        {
            throw new ValidationException(result.Errors);
        }
    }

    // Reused by the write handlers (Create/Update/ToggleHide) which already hold a materialized
    // Category entity -- GetCategoriesAsync keeps its own inline Select(...) projection since
    // that one must stay translatable by EF Core's LINQ provider.
    private static CategoryResponse ToResponse(Category c) => new(
        c.Id,
        c.Name,
        c.ParentCategoryId,
        c.Type.ToString(),
        c.Icon,
        c.Color,
        c.IsHidden,
        c.IsSystem);

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }

    private sealed record CategoryResponse(
        Guid Id,
        string Name,
        Guid? ParentCategoryId,
        string Type,
        string? Icon,
        string? Color,
        bool IsHidden,
        bool IsSystem);
}

/// <summary>
/// T-04-05: parentCategoryId, when non-null, must exist and belong to the caller. Also validates
/// Name (non-empty, <=128 chars, matching CategoryConfiguration.cs's HasMaxLength(128)) and Type
/// (must parse to a valid CategoryType). Lives in the Api project (not Application) because it
/// needs SimpleFinanceDbContext, and Application cannot reference Infrastructure without a
/// circular project reference (02-04-SUMMARY.md/03-02-SUMMARY.md/03-05-SUMMARY.md precedent). The
/// caller's UserId is threaded in via ValidationContext.RootContextData
/// (CategoriesEndpoints.ValidateWithUserAsync).
/// </summary>
public sealed class CreateCategoryValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryValidator(SimpleFinanceDbContext db)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);

        RuleFor(x => x.Type)
            .Must(type => Enum.TryParse<CategoryType>(type, ignoreCase: true, out _))
            .WithMessage("type must be one of: Expense, Income, Transfer.");

        RuleFor(x => x.ParentCategoryId)
            .MustAsync(async (_, parentCategoryId, context, ct) =>
            {
                if (parentCategoryId is null)
                {
                    return true;
                }

                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Categories.AnyAsync(c => c.Id == parentCategoryId.Value && c.UserId == userId, ct);
            })
            .WithMessage("parentCategoryId does not exist or does not belong to the caller.");
    }
}

/// <summary>
/// T-04-05: same Name/Type/parentCategoryId rules as <see cref="CreateCategoryValidator"/>, same
/// circular-reference rationale for living here instead of Application.
/// </summary>
public sealed class UpdateCategoryValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryValidator(SimpleFinanceDbContext db)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);

        RuleFor(x => x.Type)
            .Must(type => Enum.TryParse<CategoryType>(type, ignoreCase: true, out _))
            .WithMessage("type must be one of: Expense, Income, Transfer.");

        RuleFor(x => x.ParentCategoryId)
            .MustAsync(async (_, parentCategoryId, context, ct) =>
            {
                if (parentCategoryId is null)
                {
                    return true;
                }

                var userId = (Guid)context.RootContextData["UserId"]!;
                return await db.Categories.AnyAsync(c => c.Id == parentCategoryId.Value && c.UserId == userId, ct);
            })
            .WithMessage("parentCategoryId does not exist or does not belong to the caller.");
    }
}
