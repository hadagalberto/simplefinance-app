using System.Data;

namespace SimpleFinance.Infrastructure.Reporting;

/// <summary>
/// Seam over raw ADO.NET connection creation for Dapper-based reporting queries (05-04). Mirrors
/// IFinancialDataProvider's "one interface, one implementation" shape even though there is
/// currently only one provider (Postgres) — keeps DashboardQueries testable/swappable without
/// hard-coding Npgsql directly into the query class.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Returns a new, UNOPENED connection. Callers are responsible for opening (via
    /// OpenAsync) and disposing (via `await using`) it themselves.
    /// </summary>
    IDbConnection CreateConnection();
}
