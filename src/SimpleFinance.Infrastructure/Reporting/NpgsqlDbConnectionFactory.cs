using System.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace SimpleFinance.Infrastructure.Reporting;

/// <summary>
/// The only IDbConnectionFactory implementation (Postgres) — reads `ConnectionStrings:Default`
/// exactly like SimpleFinanceDbContext's own UseNpgsql call in Program.cs.
/// </summary>
public sealed class NpgsqlDbConnectionFactory(IConfiguration configuration) : IDbConnectionFactory
{
    public IDbConnection CreateConnection() =>
        new NpgsqlConnection(configuration.GetConnectionString("Default"));
}
