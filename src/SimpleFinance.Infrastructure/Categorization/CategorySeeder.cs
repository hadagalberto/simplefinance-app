using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Categorization;

/// <summary>
/// CAT-01: idempotently seeds a default BR-oriented category tree (all <c>IsSystem = true</c>)
/// and best-effort <see cref="CategoryMapping"/> rows (Pluggy category id/name -> our Category)
/// for a given user. Runs once at setup (<c>AuthEndpoints.SetupAsync</c>) and is safe to
/// re-invoke any number of times afterward (startup backstop, accidental double-call) -- see the
/// two independent idempotency guards below. Pluggy's full numeric category-id taxonomy is not
/// publicly documented (only Transfers/Transfer-TED confirmed as of this phase's research) -- the
/// seeded <see cref="CategoryMapping.PluggyCategoryId"/> values are a best-effort mix of
/// confirmed codes and normalized-slug placeholders; <see cref="CategoryMapping.PluggyCategoryName"/>
/// is always set so 04-04's rule engine can fall back to name-based matching.
/// </summary>
public sealed class CategorySeeder
{
    private readonly SimpleFinanceDbContext _db;

    public CategorySeeder(SimpleFinanceDbContext db)
    {
        _db = db;
    }

    public async Task SeedForUserAsync(Guid userId, CancellationToken ct)
    {
        // Guard 1: this user already has ANY category (seeded before, or the user created their
        // own before this ever ran) -- never re-seed or duplicate.
        if (await _db.Categories.AnyAsync(c => c.UserId == userId, ct))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var categoriesByName = new Dictionary<string, Category>(StringComparer.Ordinal);

        foreach (var seed in DefaultTree)
        {
            var parent = new Category
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = seed.Name,
                ParentCategoryId = null,
                Type = seed.Type,
                Icon = seed.Icon,
                Color = seed.Color,
                IsSystem = true,
                IsHidden = false,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Categories.Add(parent);
            categoriesByName[parent.Name] = parent;

            foreach (var child in seed.Children)
            {
                var childCategory = new Category
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Name = child.Name,
                    ParentCategoryId = parent.Id,
                    // Children inherit the parent's Type/Color; only the Icon may be more specific.
                    Type = seed.Type,
                    Icon = child.Icon,
                    Color = seed.Color,
                    IsSystem = true,
                    IsHidden = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.Categories.Add(childCategory);
                categoriesByName[childCategory.Name] = childCategory;
            }
        }

        // Guard 2: CategoryMapping has no UserId (intentional -- see its own doc comment) and is
        // effectively a global/singleton table in this single-user app. A second user's setup
        // (post-MVP, if it ever happens) still gets its own category tree above, but must never
        // duplicate mapping rows.
        if (!await _db.CategoryMappings.AnyAsync(ct))
        {
            foreach (var mapping in DefaultMappings)
            {
                if (!categoriesByName.TryGetValue(mapping.CategoryName, out var category))
                {
                    continue;
                }

                _db.CategoryMappings.Add(new CategoryMapping
                {
                    Id = Guid.NewGuid(),
                    PluggyCategoryId = mapping.PluggyCategoryId,
                    PluggyCategoryName = mapping.PluggyCategoryName,
                    CategoryId = category.Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private sealed record CategorySeed(string Name, CategoryType Type, string Icon, string Color, ChildSeed[] Children);

    private sealed record ChildSeed(string Name, string Icon);

    private sealed record MappingSeed(string PluggyCategoryId, string PluggyCategoryName, string CategoryName);

    private static readonly CategorySeed[] DefaultTree =
    [
        new("Alimentação", CategoryType.Expense, "Utensils", "#F97316",
        [
            new ChildSeed("Mercado", "ShoppingCart"),
            new ChildSeed("Restaurante e Bar", "UtensilsCrossed"),
            new ChildSeed("Delivery", "Bike"),
        ]),
        new("Transporte", CategoryType.Expense, "Car", "#EAB308",
        [
            new ChildSeed("Combustível", "Fuel"),
            new ChildSeed("Transporte Público", "Bus"),
            new ChildSeed("Aplicativo de Transporte", "CarTaxiFront"),
        ]),
        new("Moradia", CategoryType.Expense, "Home", "#84CC16",
        [
            new ChildSeed("Aluguel e Financiamento", "KeyRound"),
            new ChildSeed("Condomínio", "Building"),
            new ChildSeed("Contas (Água, Luz, Gás, Internet)", "Plug"),
        ]),
        new("Saúde", CategoryType.Expense, "HeartPulse", "#14B8A6",
        [
            new ChildSeed("Plano de Saúde", "ShieldPlus"),
            new ChildSeed("Farmácia", "Pill"),
        ]),
        new("Lazer", CategoryType.Expense, "PartyPopper", "#06B6D4",
        [
            new ChildSeed("Streaming", "Tv"),
            new ChildSeed("Viagem", "Plane"),
        ]),
        new("Educação", CategoryType.Expense, "GraduationCap", "#8B5CF6", []),
        new("Compras", CategoryType.Expense, "ShoppingBag", "#D946EF",
        [
            new ChildSeed("Vestuário", "Shirt"),
            new ChildSeed("Eletrônicos", "Smartphone"),
        ]),
        new("Serviços", CategoryType.Expense, "Wrench", "#EC4899",
        [
            new ChildSeed("Assinaturas", "Repeat"),
            new ChildSeed("Tarifas Bancárias", "Landmark"),
        ]),
        new("Renda", CategoryType.Income, "Wallet", "#16A34A",
        [
            new ChildSeed("Salário", "Briefcase"),
            new ChildSeed("Rendimentos", "TrendingUp"),
        ]),
        new("Investimentos", CategoryType.Expense, "TrendingUp", "#2563EB", []),
        new("Transferências", CategoryType.Transfer, "ArrowLeftRight", "#64748B", []),
    ];

    private static readonly MappingSeed[] DefaultMappings =
    [
        new("05000000", "Transfers", "Transferências"),
        new("05080000", "Transfer - TED", "Transferências"),
        new("groceries", "Groceries", "Mercado"),
        new("food-and-drinks", "Food and drinks", "Restaurante e Bar"),
        new("transportation", "Transportation", "Transporte"),
        new("housing", "Housing", "Moradia"),
        new("utilities", "Utilities", "Contas (Água, Luz, Gás, Internet)"),
        new("healthcare", "Healthcare", "Saúde"),
        new("leisure", "Leisure", "Lazer"),
        new("services", "Services", "Serviços"),
        new("income", "Income", "Renda"),
        new("investments", "Investments", "Investimentos"),
        new("taxes", "Taxes", "Serviços"),
        new("bank-fees", "Bank fees", "Tarifas Bancárias"),
    ];
}
