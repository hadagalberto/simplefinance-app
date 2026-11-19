using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Sync;

namespace SimpleFinance.Infrastructure.Pluggy;

/// <summary>
/// Single DI composition entry point for everything Pluggy-related. This is the ONLY thing
/// later Pluggy-adjacent plans (02-03/02-05/02-07) need to extend — keeps Program.cs edits to
/// one line per plan (02-CONTEXT.md's "one-block-per-module" pattern).
/// </summary>
public static class PluggyServiceCollectionExtensions
{
    public static IServiceCollection AddPluggyClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PluggyOptions>(configuration.GetSection(PluggyOptions.SectionName));

        // Named HttpClient (not a typed-client constructor injection) because
        // PluggyApiKeyProvider is a singleton: it resolves a client on demand via
        // IHttpClientFactory.CreateClient(HttpClientName) rather than having HttpClient
        // constructor-injected, which is only safe for transient/scoped typed clients.
        // AddStandardResilienceHandler ships sane retry/backoff/circuit-breaker defaults tuned
        // for exactly this HttpClient-wrapping use case — do not hand-roll Polly policies
        // (02-RESEARCH.md's Don't-Hand-Roll table).
        services.AddHttpClient(PluggyApiKeyProvider.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<PluggyOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        }).AddStandardResilienceHandler();

        // Singleton: the API key cache must be shared app-wide, not per-request-scoped —
        // otherwise every request would re-authenticate against Pluggy (PLG-01).
        services.AddSingleton<PluggyApiKeyProvider>();

        // PluggyClient itself uses the typed-client pattern (transient, one per resolution) —
        // it depends on the singleton PluggyApiKeyProvider for the current API key.
        services.AddHttpClient<PluggyClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<PluggyOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        }).AddStandardResilienceHandler();

        // 02-03: PluggyDataProvider is the sole implementation of IFinancialDataProvider --
        // Application-layer code (02-04's reconciler onward) depends only on this interface,
        // never on PluggyClient/PluggyDtos directly.
        services.AddScoped<IFinancialDataProvider, PluggyDataProvider>();

        return services;
    }
}
