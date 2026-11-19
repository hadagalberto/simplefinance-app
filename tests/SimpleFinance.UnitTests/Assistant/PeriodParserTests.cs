using SimpleFinance.Application.Ai.Assistant;

namespace SimpleFinance.UnitTests.Assistant;

/// <summary>
/// Unit tests for the pure <see cref="PeriodParser"/> (17-02, AI-CHAT-02) — the normalized period
/// grammar the model may emit, mapped to inclusive UTC ranges. The reference "now" is injected so the
/// relative tokens are deterministic.
/// </summary>
public class PeriodParserTests
{
    private static readonly DateTime Now = new(2026, 5, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Resolve_YearMonth_ReturnsFullInclusiveMonth()
    {
        var (start, end) = PeriodParser.Resolve("2026-05", Now);

        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 5, 31, 23, 59, 59, DateTimeKind.Utc), end);
    }

    [Fact]
    public void Resolve_Year_ReturnsFullYear()
    {
        var (start, end) = PeriodParser.Resolve("2025", Now);

        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc), end);
    }

    [Fact]
    public void Resolve_ThisMonth_UsesReferenceMonth()
    {
        var (start, end) = PeriodParser.Resolve("this_month", Now);

        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 5, 31, 23, 59, 59, DateTimeKind.Utc), end);
    }

    [Fact]
    public void Resolve_LastMonth_RollsBack()
    {
        var (start, end) = PeriodParser.Resolve("last_month", Now);

        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 4, 30, 23, 59, 59, DateTimeKind.Utc), end);
    }

    [Fact]
    public void Resolve_Last30Days_IsInclusiveWindowEndingToday()
    {
        var (start, end) = PeriodParser.Resolve("last_30_days", Now);

        Assert.Equal(new DateTime(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc), start); // 30 calendar days incl. today
        Assert.Equal(new DateTime(2026, 5, 15, 23, 59, 59, DateTimeKind.Utc), end);
    }

    [Fact]
    public void Resolve_Ytd_StartsAtJanuaryFirst()
    {
        var (start, end) = PeriodParser.Resolve("ytd", Now);

        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 5, 15, 23, 59, 59, DateTimeKind.Utc), end);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("2026-13")]
    public void Resolve_InvalidToken_Throws(string period)
    {
        Assert.Throws<FormatException>(() => PeriodParser.Resolve(period, Now));
    }

    [Fact]
    public void TryResolve_InvalidToken_ReturnsFalse()
    {
        Assert.False(PeriodParser.TryResolve("nonsense", out _, Now));
    }
}
