namespace SimpleFinance.Infrastructure.NetWorth;

/// <summary>
/// Narrow read seam over <see cref="NetWorthService"/> so the assistant's <c>net_worth</c> /
/// <c>net_worth_evolution</c> tools (17-02) can be unit-tested with a fake while production reuses the
/// real live-computed figures. Implemented by <see cref="NetWorthService"/>.
/// </summary>
public interface INetWorthReader
{
    Task<NetWorthSummaryResult> GetSummaryAsync(Guid userId, CancellationToken ct);

    Task<NetWorthEvolutionResult> GetEvolutionAsync(Guid userId, int days, CancellationToken ct);
}
