using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SimpleFinance.Infrastructure.Persistence.Conventions;

/// <summary>
/// Enforces FND-02 (UTC-only timestamps) at the EF Core value-conversion boundary.
/// Applied globally to every <see cref="DateTime"/> property via
/// SimpleFinanceDbContext.ConfigureConventions.
///
/// On write: throws if the caller passed a <see cref="DateTimeKind.Local"/> value (forces
/// callers to be explicit about UTC), and coerces <see cref="DateTimeKind.Unspecified"/> to
/// UTC via <see cref="DateTime.SpecifyKind"/>.
/// On read: Npgsql returns <see cref="DateTimeKind.Unspecified"/> for <c>timestamptz</c>
/// columns unless told otherwise, so this converter re-stamps the value as UTC.
/// </summary>
public sealed class UtcDateTimeValueConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeValueConverter()
        : base(
            toProvider => EnsureUtcForWrite(toProvider),
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc))
    {
    }

    private static DateTime EnsureUtcForWrite(DateTime value)
    {
        if (value.Kind == DateTimeKind.Local)
        {
            throw new InvalidOperationException(
                $"DateTime value '{value:O}' has Kind=Local. SimpleFinance stores all timestamps " +
                "as UTC (FND-02). Convert to UTC explicitly (e.g. DateTime.UtcNow or " +
                "value.ToUniversalTime()) before persisting.");
        }

        return value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value;
    }
}
