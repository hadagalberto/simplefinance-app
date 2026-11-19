using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleFinance.Infrastructure.Pluggy;

namespace SimpleFinance.Infrastructure.Startup;

/// <summary>
/// SYNC-01: registers Pluggy's ONE global webhook subscription exactly once per process start,
/// via 02-02's already-idempotent PluggyClient.EnsureWebhookRegisteredAsync. A Pluggy
/// outage/misconfiguration at boot must NEVER crash the app or block startup
/// (02-RESEARCH.md's explicit recommendation) -- failures are logged as a Warning and swallowed;
/// registration is retried automatically on the next restart, or via a manual admin action later.
/// </summary>
public sealed class WebhookRegistrationStartupTask : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _environment;
    private readonly PluggyOptions _options;
    private readonly ILogger<WebhookRegistrationStartupTask> _logger;

    public WebhookRegistrationStartupTask(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        IOptions<PluggyOptions> options,
        ILogger<WebhookRegistrationStartupTask> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Same "Testing" guard as Database.Migrate()/Hangfire above (EF InMemory / no real
        // Postgres in tests) -- and local dev has no public webhook URL yet (02-CONTEXT.md's
        // Specifics: "No public URL exists in dev/sandbox... webhook path is code-complete +
        // unit-tested"). PluggyClient.EnsureWebhookRegisteredAsync already no-ops on an empty
        // WebhookPublicUrl too, but checking here avoids resolving a scope/PluggyClient at all
        // in the common local-dev case.
        if (_environment.IsEnvironment("Testing") || string.IsNullOrWhiteSpace(_options.WebhookPublicUrl))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var pluggyClient = scope.ServiceProvider.GetRequiredService<PluggyClient>();
            await pluggyClient.EnsureWebhookRegisteredAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Webhook subscription registration failed at startup -- app continues to boot; will retry on next restart.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
