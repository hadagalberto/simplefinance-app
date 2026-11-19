using System.Globalization;
using System.Text.RegularExpressions;

namespace SimpleFinance.Application.Ai.Assistant;

/// <summary>
/// AI-CHAT-02: turns the normalized <c>period</c> grammar the model is allowed to emit into an
/// inclusive UTC <c>[Start, End]</c> range the reporting services consume. Grammar:
/// <c>"YYYY-MM" | "YYYY" | "last_7_days" | "last_30_days" | "last_90_days" | "this_month" |
/// "last_month" | "ytd"</c>. Ranges are inclusive on both ends and use 23:59:59 on the end day,
/// matching <c>DashboardQueries</c>'s documented period-boundary convention exactly. Pure and
/// deterministic (the reference "now" is injectable) so it is directly unit-testable.
/// </summary>
public static partial class PeriodParser
{
    /// <summary>
    /// Resolve a period token to its inclusive UTC range. Throws <see cref="FormatException"/> for a
    /// missing or unrecognized token — callers that face untrusted model input should prefer
    /// <see cref="TryResolve"/>.
    /// </summary>
    public static (DateTime Start, DateTime End) Resolve(string? period, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(period))
        {
            throw new FormatException("period is required");
        }

        var today = (now ?? DateTime.UtcNow).Date;
        var token = period.Trim().ToLowerInvariant();

        switch (token)
        {
            case "last_7_days":
                return (StartOfDay(today.AddDays(-6)), EndOfDay(today));
            case "last_30_days":
                return (StartOfDay(today.AddDays(-29)), EndOfDay(today));
            case "last_90_days":
                return (StartOfDay(today.AddDays(-89)), EndOfDay(today));
            case "this_month":
                return (StartOfMonth(today.Year, today.Month), EndOfMonth(today.Year, today.Month));
            case "last_month":
            {
                var (y, m) = PreviousMonth(today.Year, today.Month);
                return (StartOfMonth(y, m), EndOfMonth(y, m));
            }
            case "ytd":
                return (new DateTime(today.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc), EndOfDay(today));
        }

        var yearMonth = YearMonthPattern().Match(token);
        if (yearMonth.Success)
        {
            var year = int.Parse(yearMonth.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(yearMonth.Groups[2].Value, CultureInfo.InvariantCulture);
            if (month is < 1 or > 12)
            {
                throw new FormatException($"Invalid month in period '{period}'");
            }

            return (StartOfMonth(year, month), EndOfMonth(year, month));
        }

        if (YearPattern().IsMatch(token))
        {
            var year = int.Parse(token, CultureInfo.InvariantCulture);
            return (
                new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(year, 12, 31, 23, 59, 59, DateTimeKind.Utc));
        }

        throw new FormatException($"Unrecognized period '{period}'");
    }

    /// <summary>Non-throwing variant for untrusted model args — returns false on an invalid token.</summary>
    public static bool TryResolve(string? period, out (DateTime Start, DateTime End) range, DateTime? now = null)
    {
        try
        {
            range = Resolve(period, now);
            return true;
        }
        catch (FormatException)
        {
            range = default;
            return false;
        }
    }

    private static DateTime StartOfDay(DateTime date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime EndOfDay(DateTime date) =>
        new(date.Year, date.Month, date.Day, 23, 59, 59, DateTimeKind.Utc);

    private static DateTime StartOfMonth(int year, int month) =>
        new(year, month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime EndOfMonth(int year, int month) =>
        new(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59, DateTimeKind.Utc);

    private static (int Year, int Month) PreviousMonth(int year, int month) =>
        month == 1 ? (year - 1, 12) : (year, month - 1);

    [GeneratedRegex(@"^(\d{4})-(\d{2})$")]
    private static partial Regex YearMonthPattern();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex YearPattern();
}
