using Hangfire.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Application.Sync;
using SimpleFinance.Infrastructure.Alerts;
using SimpleFinance.Infrastructure.Recurring;

namespace SimpleFinance.Infrastructure.Jobs;

/// <summary>
/// DI composition entry point for the Sync module's Hangfire-invoked jobs. Registers
/// IWebhookProcessor -> WebhookProcessorJob (02-05) and DailySyncJob (02-06, the concrete class
/// itself, since RecurringJob.AddOrUpdate&lt;DailySyncJob&gt; in Program.cs resolves it directly
/// from DI rather than through an interface) -- mirrors PluggyServiceCollectionExtensions/
/// SyncServiceCollectionExtensions's established "one block per module" pattern.
/// </summary>
public static class JobsServiceCollectionExtensions
{
    public static IServiceCollection AddSyncJobs(this IServiceCollection services)
    {
        services.AddScoped<IWebhookProcessor, WebhookProcessorJob>();
        services.AddScoped<DailySyncJob>();
        services.AddScoped<NetWorthSnapshotJob>();
        services.AddScoped<RecurringDetectionJob>();
        services.AddScoped<AlertEvaluationJob>();
        services.AddScoped<AiEmbeddingJob>();
        services.AddScoped<AiCategorizationJob>();
        services.AddScoped<AiInsightJob>();
        services.AddScoped<AiAnomalyJob>();
        services.AddScoped<AiForecastJob>();
        services.AddScoped<AiMonthlySummaryJob>();

        return services;
    }
}

/// <summary>
/// Gates the Hangfire dashboard (/hangfire) behind the SAME JWT bearer scheme already wired for
/// the rest of the API -- an unauthenticated caller must never view job history/args (T-02-15).
/// Hangfire's dashboard pipeline runs its own lightweight authorization check independent of
/// ASP.NET Core's [Authorize] attribute, so this filter re-checks
/// HttpContext.User.Identity.IsAuthenticated directly -- populated by app.UseAuthentication(),
/// which Program.cs places before app.MapHangfireDashboard() in the pipeline.
/// </summary>
public sealed class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true;
    }
}
