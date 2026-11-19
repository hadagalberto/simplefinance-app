namespace SimpleFinance.Infrastructure.Investments;

/// <summary>
/// Narrow read seam over <see cref="InvestmentsService.GetSummaryAsync"/> so the assistant's
/// <c>investments_summary</c> tool (17-02) can be unit-tested with a fake while production reuses the
/// real totals + distribution computation. Implemented by <see cref="InvestmentsService"/>.
/// </summary>
public interface IInvestmentsReader
{
    Task<InvestmentsSummaryResult> GetSummaryAsync(Guid userId, CancellationToken ct);
}
