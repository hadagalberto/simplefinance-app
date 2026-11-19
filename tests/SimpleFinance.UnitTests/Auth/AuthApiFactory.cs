using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Auth;

/// <summary>
/// Boots the real SimpleFinance.Api host (Program.cs) against an isolated, per-instance
/// EF Core InMemory database instead of Postgres — mirrors 01-03's InMemory approach for
/// RefreshTokenService so these HTTP-level tests don't require a live Postgres container.
/// Each test creates its own factory instance (never shared via IClassFixture) so setup-lock
/// and other single-user-DB behaviors are never contaminated by another test's state.
/// </summary>
public class AuthApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

    public AuthApiFactory()
    {
        // The refresh cookie is marked Secure (AuthEndpoints.SetRefreshCookie). HttpClient's
        // CookieContainer correctly refuses to attach Secure cookies to non-HTTPS requests —
        // TestServer's default client base address is http://localhost, which would silently
        // drop the cookie on every subsequent request. TestServer doesn't perform real TLS
        // termination, so using an https:// base address here is purely to satisfy the
        // CookieContainer's scheme check, not an actual certificate/handshake requirement.
        ClientOptions.BaseAddress = new Uri("https://localhost");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program.cs skips Database.Migrate() (relational-only, unsupported by the InMemory
        // provider) when the environment is "Testing" — see Program.cs's startup guard.
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = "integration-test-signing-key-at-least-32-bytes-long-for-hmac256",
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "30",
                ["Cors:AllowedOrigin"] = "http://localhost:5173",
                // 01-05: AesTotpSecretProtector requires a Base64-encoded 32-byte key. Fixed
                // per-test-run value (not random) so every AuthApiFactory instance can
                // encrypt/decrypt consistently within its own isolated InMemory database.
                ["Totp:EncryptionKey"] = "IZS7xtbC5Uj4lmJzMPDBA7PWgzb5irgqIKSB9jnZnns=",
            });
        });

        builder.ConfigureServices(services =>
        {
            // EF Core 10 composes DbContextOptions<T> from every IDbContextOptionsConfiguration<T>
            // registered in the container, not just a single descriptor — removing only
            // DbContextOptions<SimpleFinanceDbContext> leaves Program.cs's UseNpgsql
            // configuration active alongside ours, causing "multiple database providers
            // registered". Remove every descriptor touching SimpleFinanceDbContext (generic or
            // not) before re-registering it against InMemory.
            var descriptorsToRemove = services
                .Where(d =>
                    d.ServiceType == typeof(SimpleFinanceDbContext)
                    || d.ServiceType == typeof(DbContextOptions<SimpleFinanceDbContext>)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().Contains(typeof(SimpleFinanceDbContext))))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<SimpleFinanceDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
        });
    }
}
