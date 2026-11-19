using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.Insights;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Ai;

/// <summary>
/// Behavior tests for <see cref="AiInsightService"/> (18-01 Task 2, AI-INS-01/03/04). Runs on the EF
/// InMemory provider. Proves the dedup-aware upsert (idempotent re-runs + dismissed-stays-dismissed via
/// the single <c>(UserId, DedupKey)</c> existence check), the UserId-scoped New-only list, and the
/// IDOR-safe dismiss (foreign/missing id mutates nothing, returns false).
/// </summary>
public class AiInsightServiceTests
{
    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AiInsight NewInsight(Guid userId, string dedupKey, AiInsightStatus status = AiInsightStatus.New)
    {
        var now = DateTime.UtcNow;
        return new AiInsight
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = AiInsightKind.SpendingChange,
            Severity = AiInsightSeverity.Info,
            Title = "Gasto acima do normal",
            Body = "Você gastou mais em Alimentação neste mês.",
            PeriodStart = now.AddDays(-30),
            PeriodEnd = now,
            DataJson = "{\"delta\":0.42}",
            Status = status,
            DedupKey = dedupKey,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    [Fact]
    public async Task UpsertAsync_NoExistingRow_InsertsAndReturnsTrue()
    {
        await using var db = CreateDbContext();
        var service = new AiInsightService(db);
        var userId = Guid.NewGuid();

        var inserted = await service.UpsertAsync(NewInsight(userId, "spend|2026-07|food"), CancellationToken.None);

        Assert.True(inserted);
        Assert.Equal(1, await db.AiInsights.CountAsync());
    }

    [Fact]
    public async Task UpsertAsync_SameDedupKeyTwice_IsIdempotentSingleRow()
    {
        await using var db = CreateDbContext();
        var service = new AiInsightService(db);
        var userId = Guid.NewGuid();

        var first = await service.UpsertAsync(NewInsight(userId, "dup"), CancellationToken.None);
        var second = await service.UpsertAsync(NewInsight(userId, "dup"), CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, await db.AiInsights.CountAsync(i => i.UserId == userId));
    }

    [Fact]
    public async Task UpsertAsync_ExistingSeenRow_DoesNotDuplicate()
    {
        await using var db = CreateDbContext();
        var service = new AiInsightService(db);
        var userId = Guid.NewGuid();
        db.AiInsights.Add(NewInsight(userId, "seen", AiInsightStatus.Seen));
        await db.SaveChangesAsync();

        var result = await service.UpsertAsync(NewInsight(userId, "seen"), CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, await db.AiInsights.CountAsync(i => i.UserId == userId));
    }

    [Fact]
    public async Task UpsertAsync_DismissedThenUpsertAgain_StaysDismissedSingleRow()
    {
        await using var db = CreateDbContext();
        var service = new AiInsightService(db);
        var userId = Guid.NewGuid();

        await service.UpsertAsync(NewInsight(userId, "occ"), CancellationToken.None);
        var stored = await db.AiInsights.SingleAsync(i => i.UserId == userId);
        await service.DismissAsync(userId, stored.Id, CancellationToken.None);

        var reUpsert = await service.UpsertAsync(NewInsight(userId, "occ"), CancellationToken.None);

        Assert.False(reUpsert);
        var rows = await db.AiInsights.Where(i => i.UserId == userId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(AiInsightStatus.Dismissed, rows[0].Status);
    }

    [Fact]
    public async Task UpsertAsync_SameDedupKeyDifferentUsers_BothInserted()
    {
        await using var db = CreateDbContext();
        var service = new AiInsightService(db);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var a = await service.UpsertAsync(NewInsight(userA, "shared"), CancellationToken.None);
        var b = await service.UpsertAsync(NewInsight(userB, "shared"), CancellationToken.None);

        Assert.True(a);
        Assert.True(b);
        Assert.Equal(2, await db.AiInsights.CountAsync());
    }

    [Fact]
    public async Task GetAsync_ReturnsOnlyCallersRowsOfStatus_NewestFirst()
    {
        await using var db = CreateDbContext();
        var service = new AiInsightService(db);
        var userId = Guid.NewGuid();
        var other = Guid.NewGuid();

        var older = NewInsight(userId, "a");
        older.CreatedAt = DateTime.UtcNow.AddHours(-2);
        var newer = NewInsight(userId, "b");
        newer.CreatedAt = DateTime.UtcNow;
        db.AiInsights.AddRange(older, newer);
        db.AiInsights.Add(NewInsight(userId, "c", AiInsightStatus.Dismissed));
        db.AiInsights.Add(NewInsight(other, "d"));
        await db.SaveChangesAsync();

        var result = await service.GetAsync(userId, AiInsightStatus.New, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.All(result, i => Assert.Equal(userId, i.UserId));
        Assert.All(result, i => Assert.Equal(AiInsightStatus.New, i.Status));
        Assert.Equal(newer.Id, result[0].Id);
        Assert.Equal(older.Id, result[1].Id);
    }

    [Fact]
    public async Task DismissAsync_OwnedRow_SetsDismissedReturnsTrue()
    {
        await using var db = CreateDbContext();
        var service = new AiInsightService(db);
        var userId = Guid.NewGuid();
        var insight = NewInsight(userId, "x");
        db.AiInsights.Add(insight);
        await db.SaveChangesAsync();

        var result = await service.DismissAsync(userId, insight.Id, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(AiInsightStatus.Dismissed, (await db.AiInsights.SingleAsync()).Status);
    }

    [Fact]
    public async Task DismissAsync_ForeignId_MutatesNothingReturnsFalse()
    {
        await using var db = CreateDbContext();
        var service = new AiInsightService(db);
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var insight = NewInsight(owner, "x");
        db.AiInsights.Add(insight);
        await db.SaveChangesAsync();

        var result = await service.DismissAsync(attacker, insight.Id, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(AiInsightStatus.New, (await db.AiInsights.SingleAsync()).Status);
    }

    [Fact]
    public async Task DismissAsync_MissingId_ReturnsFalse()
    {
        await using var db = CreateDbContext();
        var service = new AiInsightService(db);

        var result = await service.DismissAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task MarkSeenAsync_NewRow_TransitionsToSeen()
    {
        await using var db = CreateDbContext();
        var service = new AiInsightService(db);
        var userId = Guid.NewGuid();
        var insight = NewInsight(userId, "x");
        db.AiInsights.Add(insight);
        await db.SaveChangesAsync();

        var result = await service.MarkSeenAsync(userId, insight.Id, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(AiInsightStatus.Seen, (await db.AiInsights.SingleAsync()).Status);
    }
}
