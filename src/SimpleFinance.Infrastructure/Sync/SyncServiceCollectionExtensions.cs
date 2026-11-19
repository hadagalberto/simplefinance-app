using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Application.Common;
using SimpleFinance.Application.Sync;
using SimpleFinance.Infrastructure.Categorization;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Sync;

/// <summary>
/// Single DI composition entry point for the Sync/reconciliation module (mirrors
/// PluggyServiceCollectionExtensions' one-block-per-module pattern) — the ONLY thing later
/// Sync-adjacent plans (02-05/02-06/02-07) need to extend, keeping Program.cs to one line.
/// </summary>
public static class SyncServiceCollectionExtensions
{
    public static IServiceCollection AddSyncReconciler(this IServiceCollection services)
    {
        services.AddScoped<ISyncReconciler, SyncConnectionUseCase>();
        services.AddScoped<ICategorizationService, CategoryRuleEngine>();
        services.AddScoped<IDistributedLockProvider, PostgresAdvisoryLockProvider>();
        services.AddScoped<ITransferDetectionService, TransferDetectionService>();

        return services;
    }
}
