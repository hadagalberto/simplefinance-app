using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleFinance.Infrastructure.Categorization;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Startup;

/// <summary>
/// CAT-01 backstop: seeds the default category tree for any existing user with zero categories
/// at every app start (e.g. this dev environment's already-created user, or a DB restored
/// without categories). CategorySeeder.SeedForUserAsync's own per-user idempotency guard makes
/// looping every user safe and cheap for the realistic 0-or-1-user case. Mirrors
/// WebhookRegistrationStartupTask's exact contract: "Testing" environment guard, swallow-and-log
/// a Warning on failure, never crash startup.
/// </summary>
public sealed class CategorySeedStartupTask : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<CategorySeedStartupTask> _logger;

    public CategorySeedStartupTask(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        ILogger<CategorySeedStartupTask> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Same "Testing" guard as WebhookRegistrationStartupTask -- EF InMemory / no real
        // Postgres in tests, and WebApplicationFactory-based tests exercise CategorySeeder
        // directly rather than via this hosted service.
        if (_environment.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SimpleFinanceDbContext>();
            var seeder = scope.ServiceProvider.GetRequiredService<CategorySeeder>();

            var users = await db.Users.ToListAsync(cancellationToken);
            foreach (var user in users)
            {
                await seeder.SeedForUserAsync(user.Id, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Category tree seeding failed at startup -- app continues to boot; will retry on next restart.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
