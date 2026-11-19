using Dapper;
using SimpleFinance.Domain.Enums;

namespace SimpleFinance.Infrastructure.Reporting;

/// <summary>
/// One row of month-to-date AI usage rolled up per <see cref="AiFeature"/>: summed prompt/completion
/// tokens and the summed estimated USD cost. Token sums come back as <c>bigint</c> from Postgres'
/// SUM over the int token columns, hence <see cref="long"/>.
/// </summary>
public sealed record AiFeatureSpend(
    AiFeature Feature, long PromptTokens, long CompletionTokens, decimal EstimatedCostUsd);

/// <summary>
/// AI-FND-08: the hand-written Dapper aggregate behind GET /api/ai/usage — the per-feature month
/// rollup of <c>AiUsageLogs</c> the usage panel reads. Mirrors <see cref="DashboardQueries"/>'s
/// conventions exactly: (1) every query is scoped by the caller's own <c>@UserId</c> (T-15-15);
/// (2) EF Core's global soft-delete query filter does NOT apply to Dapper's raw SQL, so
/// <c>"DeletedAt" IS NULL</c> is added explicitly; (3) enum comparisons bind via named params from
/// the C# <c>(int)</c> cast, never bare literals; (4) PascalCase identifiers are quoted for Postgres.
///
/// The current-month window is a half-open <c>[MonthStartUtc, NextMonthStartUtc)</c> interval,
/// identical to <c>AiBudgetGuard</c>'s month-to-date boundary so the panel's spend total lines up
/// with the same figure the kill-switch trips on. The (UserId, CreatedAt) index (15-03) covers the
/// predicate.
/// </summary>
public class AiUsageQueries(IDbConnectionFactory connectionFactory)
{
    public virtual async Task<IReadOnlyList<AiFeatureSpend>> GetMonthlySpendByFeatureAsync(
        Guid userId, DateTime monthStartUtc, DateTime nextMonthStartUtc, CancellationToken ct)
    {
        const string sql = """
            SELECT
                l."Feature" AS "Feature",
                COALESCE(SUM(l."PromptTokens"), 0) AS "PromptTokens",
                COALESCE(SUM(l."CompletionTokens"), 0) AS "CompletionTokens",
                COALESCE(SUM(l."EstimatedCostUsd"), 0) AS "EstimatedCostUsd"
            FROM "AiUsageLogs" l
            WHERE l."UserId" = @UserId
              AND l."DeletedAt" IS NULL
              AND l."CreatedAt" >= @MonthStartUtc
              AND l."CreatedAt" < @NextMonthStartUtc
            GROUP BY l."Feature"
            ORDER BY "EstimatedCostUsd" DESC;
            """;

        var parameters = new
        {
            UserId = userId,
            MonthStartUtc = monthStartUtc,
            NextMonthStartUtc = nextMonthStartUtc,
        };

        using var connection = connectionFactory.CreateConnection();
        connection.Open();

        var command = new CommandDefinition(sql, parameters, cancellationToken: ct);
        var rows = await connection.QueryAsync<AiFeatureSpend>(command);
        return rows.ToList();
    }
}
