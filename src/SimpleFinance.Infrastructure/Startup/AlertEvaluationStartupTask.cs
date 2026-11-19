using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleFinance.Infrastructure.Alerts;

namespace SimpleFinance.Infrastructure.Startup;

/// <summary>
/// NOTIF-02/NOTIF-03 backstop: runs an initial AlertEvaluationJob pass at app boot so the
/// notification pipeline has already run once by the time the app finishes booting, rather than
/// waiting up to an hour for the first scheduled `alert-evaluation` cron run. Byte-for-byte
/// structural mirror of NetWorthSnapshotStartupTask/RecurringDetectionStartupTask: same "Testing"
/// environment guard, same fresh-DI-scope-then-call-ExecuteAsync-once shape, same swallow+
/// LogWarning catch -- AlertEvaluationJob is idempotent per (AlertRuleId, TargetKey) via its
/// AlertFireLog-backed de-dupe, so re-running it here and again on the next scheduled cron tick is
/// always safe.
/// </summary>
public sealed class AlertEvaluationStartupTask : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AlertEvaluationStartupTask> _logger;

    public AlertEvaluationStartupTask(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        ILogger<AlertEvaluationStartupTask> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Same "Testing" guard as NetWorthSnapshotStartupTask/RecurringDetectionStartupTask -- EF
        // InMemory / no real Postgres in tests, and WebApplicationFactory-based tests exercise
        // AlertEvaluationJob directly rather than via this hosted service.
        if (_environment.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<AlertEvaluationJob>();
            await job.ExecuteAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Startup alert-evaluation backstop failed -- app continues to boot; will retry on next restart / scheduled run.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
