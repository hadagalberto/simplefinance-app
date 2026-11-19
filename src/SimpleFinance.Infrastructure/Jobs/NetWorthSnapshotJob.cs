using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.Infrastructure.Jobs;

/// <summary>
/// INV-03/INV-04: the SOLE writer of NetWorthSnapshot -- a thin Hangfire-invoked adapter
/// (ARCHITECTURE.md Anti-Pattern 3, mirroring DailySyncJob's exact constructor-injection/
/// ExecuteAsync shape) that iterates every User and upserts one snapshot per (UserId, today's UTC
/// date) using the SAME net-worth formula DashboardQueries.GetTotalsAsync already uses (via
/// GetNetWorthCompositionAsync -- never a second, divergent formula). Idempotent: re-running the
/// same day updates the existing row in place rather than creating a duplicate. Also invoked once
/// at boot by NetWorthSnapshotStartupTask so the evolution chart has data immediately, not just
/// after the first scheduled cron run.
///
/// Mirrors DailySyncJob's per-connection isolation pattern (T-02-18) applied per-user here: one
/// user's compute-and-upsert failure is caught, logged with structured UserId context, and never
/// aborts the batch -- every other user must still get their snapshot written. SaveChangesAsync is
/// called once per user (not batched across the whole loop) so one user's failure can't roll back
/// another's already-saved snapshot.
/// </summary>
public sealed class NetWorthSnapshotJob
{
    private readonly SimpleFinanceDbContext _db;
    private readonly DashboardQueries _dashboardQueries;
    private readonly ILogger<NetWorthSnapshotJob> _logger;

    public NetWorthSnapshotJob(
        SimpleFinanceDbContext db, DashboardQueries dashboardQueries, ILogger<NetWorthSnapshotJob> logger)
    {
        _db = db;
        _dashboardQueries = dashboardQueries;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        // Users has no DeletedAt at all (User : AuditableEntity, not ISoftDelete) -- no
        // IgnoreQueryFilters needed, db.Users already returns every user as-is.
        var users = await _db.Users.ToListAsync(ct);
        var today = DateTime.UtcNow.Date;

        foreach (var user in users)
        {
            try
            {
                var composition = await _dashboardQueries.GetNetWorthCompositionAsync(user.Id, ct);

                var totalAssets = composition.AccountsBalance + composition.InvestmentsBalance;
                var totalLiabilities = composition.CreditCardDebt;

                var snapshot = await _db.NetWorthSnapshots
                    .FirstOrDefaultAsync(s => s.UserId == user.Id && s.Date == today, ct);

                if (snapshot is null)
                {
                    _db.NetWorthSnapshots.Add(new NetWorthSnapshot
                    {
                        Id = Guid.NewGuid(),
                        UserId = user.Id,
                        Date = today,
                        TotalAssets = totalAssets,
                        TotalLiabilities = totalLiabilities,
                        NetWorth = composition.NetWorth,
                        AccountsBalance = composition.AccountsBalance,
                        InvestmentsBalance = composition.InvestmentsBalance,
                        CreditCardDebt = composition.CreditCardDebt,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    });
                }
                else
                {
                    // Update the tracked entity in place -- never create-then-delete-old.
                    snapshot.TotalAssets = totalAssets;
                    snapshot.TotalLiabilities = totalLiabilities;
                    snapshot.NetWorth = composition.NetWorth;
                    snapshot.AccountsBalance = composition.AccountsBalance;
                    snapshot.InvestmentsBalance = composition.InvestmentsBalance;
                    snapshot.CreditCardDebt = composition.CreditCardDebt;
                    snapshot.UpdatedAt = DateTime.UtcNow;
                }

                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // T-06-06: never let one user's failure abort the batch -- log structured context
                // (UserId included) and move on to the next user. ChangeTracker.Clear() discards
                // this user's un-saved (Added/Modified) snapshot entity so it can never leak into
                // and re-fail the NEXT user's SaveChangesAsync call -- without this, a failed save
                // would leave its entity tracked, silently bundling it into (and repeating the
                // failure for) every subsequent user's upsert in the same batch.
                _logger.LogError(ex, "NetWorthSnapshotJob failed to snapshot net worth for user {UserId}", user.Id);
                _db.ChangeTracker.Clear();
            }
        }
    }
}
