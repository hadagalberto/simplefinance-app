using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleFinance.Infrastructure.Recurring;

namespace SimpleFinance.Infrastructure.Startup;

/// <summary>
/// RECUR-01 backstop: runs an initial RecurringDetectionJob pass at app boot so the recurring
/// panel has data immediately, not just after the first scheduled `recurring-detection` cron run
/// (which could be up to a week away on the default weekly cadence). Byte-for-byte structural
/// mirror of NetWorthSnapshotStartupTask: same "Testing" environment guard, same fresh-DI-scope-
/// then-call-ExecuteAsync-once shape, same swallow+LogWarning catch -- RecurringDetectionJob is
/// idempotent per (user, NormalizedMerchant), so re-running it here and again on the next
/// scheduled cron tick is always safe.
/// </summary>
public sealed class RecurringDetectionStartupTask : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<RecurringDetectionStartupTask> _logger;

    public RecurringDetectionStartupTask(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        ILogger<RecurringDetectionStartupTask> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Same "Testing" guard as NetWorthSnapshotStartupTask -- EF InMemory / no real Postgres in
        // tests, and WebApplicationFactory-based tests exercise RecurringDetectionJob directly
        // rather than via this hosted service.
        if (_environment.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<RecurringDetectionJob>();
            await job.ExecuteAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Startup recurring-detection backstop failed -- app continues to boot; will retry on next restart / scheduled run.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
