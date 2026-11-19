using Microsoft.EntityFrameworkCore;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai.Assistant;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.UnitTests.Assistant;

/// <summary>
/// Behavior tests for <see cref="ConversationService"/> (17-01 Task 2, AI-CHAT-01). Runs on the EF
/// InMemory provider. Asserts the UserId-scoped conversation CRUD + message-append contract the
/// orchestrator (17-04) and endpoints (17-05) build on: create, get-with-messages (ordered), list
/// (recency-ordered, soft-delete-excluded), rename, soft-delete, and append — plus the foreign-UserId
/// no-leak / no-mutation cases (T-17-01/02).
/// </summary>
public class ConversationServiceTests
{
    private static SimpleFinanceDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SimpleFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task CreateAsync_ReturnsConversationOwnedByUser_WithTimestamps()
    {
        await using var db = CreateDbContext();
        var service = new ConversationService(db);
        var userId = Guid.NewGuid();

        var conversation = await service.CreateAsync(userId, "Gastos de julho", CancellationToken.None);

        Assert.NotEqual(Guid.Empty, conversation.Id);
        Assert.Equal(userId, conversation.UserId);
        Assert.Equal("Gastos de julho", conversation.Title);
        Assert.NotEqual(default, conversation.CreatedAt);
        Assert.NotEqual(default, conversation.UpdatedAt);

        var persisted = await db.AiConversations.FindAsync(conversation.Id);
        Assert.NotNull(persisted);
    }

    [Fact]
    public async Task GetWithMessagesAsync_ReturnsConversationAndMessagesOrderedByCreatedAt()
    {
        await using var db = CreateDbContext();
        var service = new ConversationService(db);
        var userId = Guid.NewGuid();

        var conversation = await service.CreateAsync(userId, "Conversa", CancellationToken.None);

        // Insert two messages out of chronological order to prove the service orders them.
        var older = DateTime.UtcNow.AddMinutes(-5);
        var newer = DateTime.UtcNow;
        db.AiMessages.AddRange(
            new AiMessage { Id = Guid.NewGuid(), UserId = userId, ConversationId = conversation.Id, Role = MessageRole.Assistant, Content = "second", CreatedAt = newer, UpdatedAt = newer },
            new AiMessage { Id = Guid.NewGuid(), UserId = userId, ConversationId = conversation.Id, Role = MessageRole.User, Content = "first", CreatedAt = older, UpdatedAt = older });
        await db.SaveChangesAsync();

        var result = await service.GetWithMessagesAsync(userId, conversation.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(conversation.Id, result!.Conversation.Id);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("first", result.Messages[0].Content);
        Assert.Equal("second", result.Messages[1].Content);
    }

    [Fact]
    public async Task GetWithMessagesAsync_ForeignUser_ReturnsNull_NoLeak()
    {
        await using var db = CreateDbContext();
        var service = new ConversationService(db);
        var ownerId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();

        var conversation = await service.CreateAsync(ownerId, "Privada", CancellationToken.None);

        var result = await service.GetWithMessagesAsync(foreignId, conversation.Id, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_ReturnsUsersConversations_OrderedByUpdatedAtDescending_ExcludingSoftDeleted()
    {
        await using var db = CreateDbContext();
        var service = new ConversationService(db);
        var userId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        var oldest = await service.CreateAsync(userId, "Antiga", CancellationToken.None);
        var middle = await service.CreateAsync(userId, "Meio", CancellationToken.None);
        var newest = await service.CreateAsync(userId, "Nova", CancellationToken.None);
        await service.CreateAsync(otherId, "De outro usuário", CancellationToken.None);

        // Force distinct UpdatedAt values (deterministic ordering) and soft-delete the middle one.
        oldest.UpdatedAt = DateTime.UtcNow.AddMinutes(-10);
        middle.UpdatedAt = DateTime.UtcNow.AddMinutes(-5);
        newest.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await service.DeleteAsync(userId, middle.Id, CancellationToken.None);

        var list = await service.ListAsync(userId, CancellationToken.None);

        Assert.Equal(2, list.Count);
        Assert.Equal(newest.Id, list[0].Id);
        Assert.Equal(oldest.Id, list[1].Id);
    }

    [Fact]
    public async Task RenameAsync_UpdatesTitleAndUpdatedAt()
    {
        await using var db = CreateDbContext();
        var service = new ConversationService(db);
        var userId = Guid.NewGuid();

        var conversation = await service.CreateAsync(userId, "Sem título", CancellationToken.None);
        conversation.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        var before = conversation.UpdatedAt;

        var renamed = await service.RenameAsync(userId, conversation.Id, "Orçamento 2026", CancellationToken.None);

        Assert.True(renamed);
        var reloaded = await db.AiConversations.FindAsync(conversation.Id);
        Assert.Equal("Orçamento 2026", reloaded!.Title);
        Assert.True(reloaded.UpdatedAt > before);
    }

    [Fact]
    public async Task RenameAsync_ForeignId_ReturnsFalse_NoMutation()
    {
        await using var db = CreateDbContext();
        var service = new ConversationService(db);
        var ownerId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();

        var conversation = await service.CreateAsync(ownerId, "Original", CancellationToken.None);

        var renamed = await service.RenameAsync(foreignId, conversation.Id, "Hackeada", CancellationToken.None);

        Assert.False(renamed);
        var reloaded = await db.AiConversations.FindAsync(conversation.Id);
        Assert.Equal("Original", reloaded!.Title);
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletes_RemovesFromList()
    {
        await using var db = CreateDbContext();
        var service = new ConversationService(db);
        var userId = Guid.NewGuid();

        var conversation = await service.CreateAsync(userId, "A remover", CancellationToken.None);

        var deleted = await service.DeleteAsync(userId, conversation.Id, CancellationToken.None);

        Assert.True(deleted);
        var list = await service.ListAsync(userId, CancellationToken.None);
        Assert.Empty(list);
        // Soft-delete, not hard-delete: the row still exists (ignoring the global filter).
        var raw = await db.AiConversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversation.Id);
        Assert.NotNull(raw);
        Assert.NotNull(raw!.DeletedAt);
    }

    [Fact]
    public async Task DeleteAsync_ForeignId_ReturnsFalse()
    {
        await using var db = CreateDbContext();
        var service = new ConversationService(db);
        var ownerId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();

        var conversation = await service.CreateAsync(ownerId, "Protegida", CancellationToken.None);

        var deleted = await service.DeleteAsync(foreignId, conversation.Id, CancellationToken.None);

        Assert.False(deleted);
        var raw = await db.AiConversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversation.Id);
        Assert.Null(raw!.DeletedAt);
    }

    [Fact]
    public async Task AppendMessageAsync_InsertsMessageScopedToUser_AndBumpsConversationUpdatedAt()
    {
        await using var db = CreateDbContext();
        var service = new ConversationService(db);
        var userId = Guid.NewGuid();

        var conversation = await service.CreateAsync(userId, "Conversa", CancellationToken.None);
        conversation.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        var before = conversation.UpdatedAt;

        var message = await service.AppendMessageAsync(
            userId, conversation.Id, MessageRole.User, "Quanto gastei em julho?", ct: CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal(userId, message!.UserId);
        Assert.Equal(conversation.Id, message.ConversationId);
        Assert.Equal(MessageRole.User, message.Role);
        Assert.Equal("Quanto gastei em julho?", message.Content);

        var reloaded = await db.AiConversations.FindAsync(conversation.Id);
        Assert.True(reloaded!.UpdatedAt > before);
    }

    [Fact]
    public async Task AppendMessageAsync_ForeignConversation_ReturnsNull_NoInsert()
    {
        await using var db = CreateDbContext();
        var service = new ConversationService(db);
        var ownerId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();

        var conversation = await service.CreateAsync(ownerId, "Privada", CancellationToken.None);

        var message = await service.AppendMessageAsync(
            foreignId, conversation.Id, MessageRole.User, "leak?", ct: CancellationToken.None);

        Assert.Null(message);
        Assert.Empty(db.AiMessages);
    }
}
