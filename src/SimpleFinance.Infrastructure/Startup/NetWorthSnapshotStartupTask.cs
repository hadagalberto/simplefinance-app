using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleFinance.Infrastructure.Jobs;

namespace SimpleFinance.Infrastructure.Startup;

/// <summary>
/// INV-04 backstop: writes an initial NetWorthSnapshot at app boot so the evolution chart has
/// data immediately, not just after the first scheduled `net-worth-snapshot` cron run (which
/// could be up to 24h away). Mirrors CategorySeedStartupTask.cs's exact contract: "Testing"
/// environment guard, resolves the job from a fresh DI scope, calls it once, and swallows+logs
/// (Warning level) any exception rather than crashing app startup -- NetWorthSnapshotJob is
/// idempotent per (user, date), so re-running it here and again on the next scheduled cron tick
/// is always safe.
/// </summary>
public sealed class NetWorthSnapshotStartupTask : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<NetWorthSnapshotStartupTask> _logger;

    public NetWorthSnapshotStartupTask(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        ILogger<NetWorthSnapshotStartupTask> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Same "Testing" guard as CategorySeedStartupTask -- EF InMemory / no real Postgres in
        // tests, and WebApplicationFactory-based tests exercise NetWorthSnapshotJob directly
        // rather than via this hosted service.
        if (_environment.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<NetWorthSnapshotJob>();
            await job.ExecuteAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Startup net-worth snapshot backstop failed -- app continues to boot; will retry on next restart / scheduled run.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
