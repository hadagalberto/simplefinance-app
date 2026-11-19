using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Backup;

/// <summary>
/// CFG-03: a full, user-scoped JSON data dump for backup purposes -- every user-owned financial
/// table this app has, and NOTHING from Users/RefreshTokens/RecoveryCodes (T-08-13's mitigation:
/// those tables are never queried by this class at all, so PasswordHash/TotpSecret/refresh-token
/// hashes can never appear in a dump regardless of future entity changes). Deliberately placed in
/// Infrastructure (not Application), mirroring BudgetService/PreferencesService's documented
/// rationale: it depends directly on SimpleFinanceDbContext, and Application cannot reference
/// Infrastructure without a circular project reference. BackupEndpoints.cs is a thin HTTP wrapper
/// over this class only -- no business logic duplicated there.
/// </summary>
public sealed class BackupService(SimpleFinanceDbContext db)
{
    // camelCase to match the rest of this API's JSON convention (ASP.NET Core's Results.Ok goes
    // through the framework's own camelCase-default JsonOptions; this is a raw
    // JsonSerializer.Serialize call outside that pipeline, so the policy is set explicitly here).
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Builds the full backup dump as an indented JSON string: an `exportedAt` timestamp plus
    /// one array field per user-owned table, each queried scoped by `UserId == userId` and
    /// serialized as full entity rows (raw backup dump, not a display-shaped API response --
    /// no per-table DTO mapping). Connection rows are included as-is: Connection carries no
    /// Pluggy API credential, only the Item's own non-secret identifiers (02-01's entity design),
    /// so no redaction is needed there either.
    /// </summary>
    public async Task<string> BuildJsonDumpAsync(Guid userId, CancellationToken ct)
    {
        var dump = new
        {
            exportedAt = DateTime.UtcNow,
            accounts = await db.Accounts.Where(x => x.UserId == userId).ToListAsync(ct),
            transactions = await db.Transactions.Where(x => x.UserId == userId).ToListAsync(ct),
            categories = await db.Categories.Where(x => x.UserId == userId).ToListAsync(ct),
            categoryRules = await db.CategoryRules.Where(x => x.UserId == userId).ToListAsync(ct),
            budgets = await db.Budgets.Where(x => x.UserId == userId).ToListAsync(ct),
            goals = await db.Goals.Where(x => x.UserId == userId).ToListAsync(ct),
            recurringTransactions = await db.RecurringTransactions.Where(x => x.UserId == userId).ToListAsync(ct),
            investments = await db.Investments.Where(x => x.UserId == userId).ToListAsync(ct),
            connections = await db.Connections.Where(x => x.UserId == userId).ToListAsync(ct),
            alertRules = await db.AlertRules.Where(x => x.UserId == userId).ToListAsync(ct),
            netWorthSnapshots = await db.NetWorthSnapshots.Where(x => x.UserId == userId).ToListAsync(ct),
        };

        return JsonSerializer.Serialize(dump, SerializerOptions);
    }
}
