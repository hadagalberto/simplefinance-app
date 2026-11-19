using Microsoft.Extensions.Configuration;
using Npgsql;
using SimpleFinance.Application.Common;

namespace SimpleFinance.Infrastructure.Persistence;

/// <summary>
/// SYNC-07's real distributed lock: a Postgres SESSION-level advisory lock
/// (<c>pg_advisory_lock(hashtext(key))</c>), acquired on a dedicated <see cref="NpgsqlConnection"/>
/// opened independently of the ambient <see cref="SimpleFinanceDbContext"/> connection — so the
/// lock's lifetime is entirely decoupled from any single <c>SaveChangesAsync</c> call and survives
/// for the full duration of the caller's <c>await using</c> block. <c>pg_advisory_lock</c> BLOCKS
/// the calling session until the lock is free (never errors/rejects), which is the intended
/// behavior: a second sync for the same connection WAITS rather than failing, satisfying SYNC-07's
/// "serialized" requirement. Chosen over Hangfire's <c>DisableConcurrentExecution</c> attribute
/// per ARCHITECTURE.md Anti-Pattern 2 / RESEARCH.md's Claude's Discretion note, so the reconciler
/// itself enforces serialization regardless of caller (webhook processor, DailySyncJob, or a
/// future manual-sync endpoint all funnel through the same lock).
/// </summary>
public sealed class PostgresAdvisoryLockProvider : IDistributedLockProvider
{
    // Read the ORIGINAL connection string from configuration (with the password), mirroring
    // NpgsqlDbConnectionFactory. Do NOT derive it from the DbContext's live connection: Npgsql strips
    // the password from NpgsqlConnection.ConnectionString after opening (Persist Security Info=false),
    // so a dedicated connection built from that stripped string fails SCRAM auth against a
    // password-protected Postgres ("No password has been provided") — which broke every sync job.
    private readonly string _connectionString;

    public PostgresAdvisoryLockProvider(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
    }

    public async Task<IAsyncDisposable> AcquireAsync(string key, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        try
        {
            await using var lockCommand = connection.CreateCommand();
            lockCommand.CommandText = "SELECT pg_advisory_lock(hashtext(@key));";
            lockCommand.Parameters.AddWithValue("key", key);
            await lockCommand.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return new AdvisoryLockHandle(connection, key);
    }

    /// <summary>
    /// Releases the advisory lock (<c>pg_advisory_unlock</c>) and closes the dedicated connection
    /// on disposal. Unlock is attempted best-effort even if it fails — the underlying connection
    /// is always closed, which also releases every session-level advisory lock it holds as a
    /// Postgres-side safety net.
    /// </summary>
    private sealed class AdvisoryLockHandle : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly string _key;

        public AdvisoryLockHandle(NpgsqlConnection connection, string key)
        {
            _connection = connection;
            _key = key;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var unlockCommand = _connection.CreateCommand();
                unlockCommand.CommandText = "SELECT pg_advisory_unlock(hashtext(@key));";
                unlockCommand.Parameters.AddWithValue("key", _key);
                await unlockCommand.ExecuteNonQueryAsync();
            }
            finally
            {
                await _connection.DisposeAsync();
            }
        }
    }
}
