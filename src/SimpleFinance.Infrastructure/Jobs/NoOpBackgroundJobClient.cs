using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace SimpleFinance.Infrastructure.Jobs;

/// <summary>
/// Registered ONLY under the "Testing" ASPNETCORE_ENVIRONMENT (Program.cs), where the real
/// Hangfire server/storage is never wired up (Hangfire.PostgreSql doesn't work against EF Core's
/// InMemory provider). Its sole purpose is to make <see cref="IBackgroundJobClient"/> resolvable
/// via DI for every WebApplicationFactory-based test host -- without SOME registration, ASP.NET
/// Core's minimal-API RequestDelegateFactory can't classify an endpoint handler's
/// IBackgroundJobClient parameter as a DI service (IServiceProviderIsService returns false),
/// and if that same handler also has a genuine JSON body parameter, both become ambiguous
/// "inferred body" candidates -- a hard startup crash ("Failure to infer one or more
/// parameters"), not just a test-time inconvenience (02-07's ConnectionsEndpointsTests surfaced
/// this pre-existing gap: WebhookEndpoints/SyncEndpoints happened not to trip it only because
/// neither of their handlers has a second Body-eligible parameter to collide with).
/// Test classes that assert on enqueued jobs (SyncApiFactory/WebhookApiFactory/
/// ConnectionsApiFactory) override this with their own FakeBackgroundJobClient via
/// RemoveAll&lt;IBackgroundJobClient&gt;() + AddSingleton -- this no-op is only ever observed by
/// factories (e.g. AuthApiFactory) that don't care about background jobs at all.
/// </summary>
public sealed class NoOpBackgroundJobClient : IBackgroundJobClient
{
    public string Create(Job job, IState state) => Guid.NewGuid().ToString("N");

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}
