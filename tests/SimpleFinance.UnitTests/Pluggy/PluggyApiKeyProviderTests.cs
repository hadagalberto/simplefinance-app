using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Options;
using SimpleFinance.Infrastructure.Pluggy;
using SimpleFinance.Infrastructure.Pluggy.Dtos;

namespace SimpleFinance.UnitTests.Pluggy;

/// <summary>
/// Covers PLG-01 via a fake HttpMessageHandler — no live Pluggy sandbox credentials needed
/// (02-CONTEXT.md's "Claude's Discretion" note on mocking Pluggy for tests).
/// </summary>
public class PluggyApiKeyProviderTests
{
    // Genuinely async (never blocks the calling thread synchronously) so that a handler which
    // itself awaits an incomplete Task (e.g. an unreleased gate) actually suspends and hands
    // control back to the caller — a synchronous Task.FromResult/blocking-Wait implementation
    // would run the whole request chain to completion on the calling thread before the test
    // could start a genuinely concurrent second call.
    private sealed class CountingHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _callCount);
            return await respond(request, n);
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static HttpResponseMessage AuthOkResponse(string apiKey) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new PluggyAuthResponse(apiKey)),
    };

    private static PluggyApiKeyProvider CreateSut(CountingHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api-sandbox.pluggy.ai") };
        var factory = new FakeHttpClientFactory(httpClient);
        var options = Options.Create(new PluggyOptions { ClientId = "client-id", ClientSecret = "client-secret" });
        return new PluggyApiKeyProvider(factory, options);
    }

    private static void SetIssuedAt(PluggyApiKeyProvider provider, DateTimeOffset issuedAt)
    {
        var issuedAtField = typeof(PluggyApiKeyProvider).GetField("_issuedAt", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Expected private field _issuedAt on PluggyApiKeyProvider.");
        issuedAtField.SetValue(provider, issuedAt);

        var expiresAtField = typeof(PluggyApiKeyProvider).GetField("_expiresAt", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Expected private field _expiresAt on PluggyApiKeyProvider.");
        expiresAtField.SetValue(provider, issuedAt.AddHours(2));
    }

    [Fact]
    public async Task GetApiKeyAsync_CalledTwiceWithinTtlMargin_ReturnsCachedKeyWithoutSecondAuthCall()
    {
        var handler = new CountingHandler((_, _) => Task.FromResult(AuthOkResponse("key-1")));
        var sut = CreateSut(handler);

        var first = await sut.GetApiKeyAsync(CancellationToken.None);
        var second = await sut.GetApiKeyAsync(CancellationToken.None);

        Assert.Equal("key-1", first);
        Assert.Equal("key-1", second);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetApiKeyAsync_CalledWhenCachedKeyIsWithin30MinutesOfExpiry_RefreshesWithFreshAuthCall()
    {
        var handler = new CountingHandler((_, n) => Task.FromResult(AuthOkResponse($"key-{n}")));
        var sut = CreateSut(handler);

        var first = await sut.GetApiKeyAsync(CancellationToken.None);

        // Simulate 91 minutes elapsed since issue (2h TTL, 30min margin -> due for refresh at
        // ~90min per 02-RESEARCH.md's Code Examples). No injectable clock seam exists, so this
        // reaches into the private timestamp fields directly rather than sleeping for real.
        SetIssuedAt(sut, DateTimeOffset.UtcNow.AddMinutes(-91));

        var second = await sut.GetApiKeyAsync(CancellationToken.None);

        Assert.Equal("key-1", first);
        Assert.Equal("key-2", second);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ForceRefreshAsync_ConcurrentCallers_ShareOneAuthCallAndReceiveSameRefreshedKey()
    {
        var gate = new SemaphoreSlim(0, 1);
        var handler = new CountingHandler(async (_, n) =>
        {
            if (n == 1)
            {
                // Suspend the first in-flight call (a genuine async await, not a blocking Wait)
                // so the second caller genuinely races it while the SemaphoreSlim-guarded
                // refresh is in progress.
                await gate.WaitAsync(TimeSpan.FromSeconds(5));
            }

            return AuthOkResponse($"key-{n}");
        });
        var sut = CreateSut(handler);

        var task1 = sut.ForceRefreshAsync(CancellationToken.None);
        await Task.Delay(50); // let task1 acquire the internal lock and enter the blocked HTTP call first
        var task2 = sut.ForceRefreshAsync(CancellationToken.None);
        gate.Release();

        var results = await Task.WhenAll(task1, task2);

        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, handler.CallCount);
    }
}
