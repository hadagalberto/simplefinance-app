using Microsoft.Extensions.DependencyInjection;

namespace SimpleFinance.Infrastructure.Reporting;

/// <summary>
/// Single DI composition entry point for the Reporting module (mirrors
/// SyncServiceCollectionExtensions/PluggyServiceCollectionExtensions' one-block-per-module
/// pattern) — the ONLY thing later Reporting-adjacent plans (05-05/05-06) need to extend.
/// </summary>
public static class ReportingServiceCollectionExtensions
{
    public static IServiceCollection AddReporting(this IServiceCollection services)
    {
        // Both are stateless: the factory only reads the connection string via IConfiguration,
        // and DashboardQueries only holds a reference to the factory — Npgsql's own internal
        // connection pooling handles concurrent connections safely, so Singleton is correct here
        // (no per-request state, unlike SimpleFinanceDbContext's Scoped lifetime).
        services.AddSingleton<IDbConnectionFactory, NpgsqlDbConnectionFactory>();
        services.AddSingleton<DashboardQueries>();
        services.AddSingleton<InvestmentQueries>();
        services.AddSingleton<ReportQueries>();
        services.AddSingleton<AiUsageQueries>();

        return services;
    }
}
