using SimpleFinance.Infrastructure.Reporting;

namespace SimpleFinance.Infrastructure.NetWorth;

public sealed record NetWorthSummaryResult(
    decimal AccountsBalance, decimal InvestmentsBalance, decimal CreditCardDebt, decimal NetWorth);

public sealed record NetWorthEvolutionPointResult(DateTime Date, decimal NetWorth);

public sealed record NetWorthEvolutionResult(IReadOnlyList<NetWorthEvolutionPointResult> Points);

/// <summary>
/// The real, directly-testable Net Worth business logic (06-04 Task 3) behind GET /api/networth/*
/// -- NetWorthEndpoints.cs is a thin HTTP wrapper over this class only, no business logic
/// duplicated there.
/// </summary>
public sealed class NetWorthService(DashboardQueries dashboardQueries) : INetWorthReader
{
    /// <summary>
    /// A thin passthrough to GetNetWorthCompositionAsync -- the SAME live-computed figure
    /// NetWorthSnapshotJob would write today, not a stale read of yesterday's snapshot.
    /// </summary>
    public async Task<NetWorthSummaryResult> GetSummaryAsync(Guid userId, CancellationToken ct)
    {
        var composition = await dashboardQueries.GetNetWorthCompositionAsync(userId, ct);

        return new NetWorthSummaryResult(
            composition.AccountsBalance, composition.InvestmentsBalance, composition.CreditCardDebt, composition.NetWorth);
    }

    /// <summary>
    /// Maps days to rangeStart = UtcNow.Date.AddDays(-days), then delegates directly to the
    /// ALREADY-EXISTING DashboardQueries.GetNetWorthEvolutionAsync (05-04, unchanged) -- never
    /// duplicated or reimplemented. Returns whatever points exist (0/1/2+) -- no gap-filling,
    /// matching GetNetWorthEvolutionAsync's already-established "frontend's concern" contract.
    /// </summary>
    public async Task<NetWorthEvolutionResult> GetEvolutionAsync(Guid userId, int days, CancellationToken ct)
    {
        var rangeStart = DateTime.UtcNow.Date.AddDays(-days);
        var rows = await dashboardQueries.GetNetWorthEvolutionAsync(userId, rangeStart, ct);

        var points = rows.Select(row => new NetWorthEvolutionPointResult(row.Date, row.NetWorth)).ToList();
        return new NetWorthEvolutionResult(points);
    }
}
