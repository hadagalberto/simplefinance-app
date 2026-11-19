using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Ai;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;

namespace SimpleFinance.Infrastructure.Ai;

/// <summary>
/// AI-CAT-06: the review-loop operations behind GET /api/ai/suggestions and the accept/reject/
/// bulk-accept endpoints. Reads the pending <see cref="AiSuggestion"/> queue (joined to its
/// transaction + resolved category name) and applies the user's decisions. Deliberately in
/// Infrastructure (not Application) — it depends directly on <see cref="SimpleFinanceDbContext"/>,
/// mirroring <see cref="AiSettingsService"/>'s documented circular-reference rationale.
///
/// EVERY query is UserId-scoped (T-16-11) and every accept/reject loads the suggestion scoped to the
/// caller, so a foreign or non-existent id simply "isn't found" — no mutation, no leak (T-16-12).
/// Accepting applies the suggested category with <see cref="CategorySource.AiSuggested"/>: a
/// user-confirmed label the categorization job's never-overwrite guard already excludes AND a trusted
/// positive kNN neighbour (16-03 voter trusts AiSuggested) — the cheap learning loop, no fine-tuning.
/// The global soft-delete query filter excludes <c>DeletedAt != null</c> rows automatically.
/// </summary>
public sealed class AiSuggestionService(SimpleFinanceDbContext db)
{
    /// <summary>
    /// The user's pending suggestion queue, oldest first. Each row is joined to its target transaction
    /// (description/merchant) and the suggested category's name (parsed out of the jsonb payload).
    /// Backed by the <c>(UserId, Status)</c> index.
    /// </summary>
    public async Task<IReadOnlyList<SuggestionResponse>> GetPendingAsync(Guid userId, CancellationToken ct)
    {
        var suggestions = await db.AiSuggestions
            .Where(s => s.UserId == userId && s.Status == AiSuggestionStatus.Pending)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        if (suggestions.Count == 0)
        {
            return [];
        }

        // Resolve target transactions (UserId-scoped) for description/merchant projection.
        var transactionIds = suggestions
            .Where(s => s.TransactionId is not null)
            .Select(s => s.TransactionId!.Value)
            .Distinct()
            .ToList();

        var transactions = await db.Transactions
            .Where(t => t.UserId == userId && transactionIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        // Resolve suggested-category names (UserId-scoped) by parsing categoryId out of each payload.
        var categoryIds = suggestions
            .Select(s => TryParseCategoryId(s.SuggestedPayload))
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var categoryNames = await db.Categories
            .Where(c => c.UserId == userId && categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var result = new List<SuggestionResponse>(suggestions.Count);
        foreach (var s in suggestions)
        {
            Transaction? txn = null;
            if (s.TransactionId is not null)
            {
                transactions.TryGetValue(s.TransactionId.Value, out txn);
            }

            var categoryId = TryParseCategoryId(s.SuggestedPayload);
            string? categoryName = null;
            if (categoryId is not null)
            {
                categoryNames.TryGetValue(categoryId.Value, out categoryName);
            }

            result.Add(new SuggestionResponse(
                s.Id,
                s.TransactionId,
                txn?.Description ?? string.Empty,
                txn?.MerchantName,
                categoryId,
                categoryName,
                s.Confidence,
                s.Rationale,
                s.Source.ToString(),
                s.Status.ToString(),
                s.CreatedAt));
        }

        return result;
    }

    /// <summary>
    /// Accept a single pending suggestion: apply its category to the transaction as
    /// <see cref="CategorySource.AiSuggested"/> and mark the suggestion Accepted. Returns false when
    /// the id is missing, not owned, not pending, or the target transaction can't be resolved — the
    /// endpoint maps that to 404 with no mutation (T-16-12).
    /// </summary>
    public async Task<bool> AcceptAsync(Guid userId, Guid suggestionId, CancellationToken ct)
    {
        var suggestion = await db.AiSuggestions
            .FirstOrDefaultAsync(
                s => s.Id == suggestionId && s.UserId == userId && s.Status == AiSuggestionStatus.Pending,
                ct);

        if (suggestion is null)
        {
            return false;
        }

        var applied = await TryApplyAcceptAsync(userId, suggestion, DateTime.UtcNow, ct);
        if (applied)
        {
            await db.SaveChangesAsync(ct);
        }

        return applied;
    }

    /// <summary>
    /// Reject a single pending suggestion: mark it Rejected + stamp DecidedAt; the transaction is left
    /// untouched ("Sem categoria"). Returns false when the id is missing/not owned/not pending.
    /// </summary>
    public async Task<bool> RejectAsync(Guid userId, Guid suggestionId, CancellationToken ct)
    {
        var suggestion = await db.AiSuggestions
            .FirstOrDefaultAsync(
                s => s.Id == suggestionId && s.UserId == userId && s.Status == AiSuggestionStatus.Pending,
                ct);

        if (suggestion is null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        suggestion.Status = AiSuggestionStatus.Rejected;
        suggestion.DecidedAt = now;
        suggestion.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Accept a batch of pending suggestions in one save. Foreign/unknown/non-pending ids are silently
    /// skipped (never mutated); returns the count actually accepted.
    /// </summary>
    public async Task<int> BulkAcceptAsync(Guid userId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var distinctIds = ids.Distinct().ToList();

        var suggestions = await db.AiSuggestions
            .Where(s => s.UserId == userId
                        && s.Status == AiSuggestionStatus.Pending
                        && distinctIds.Contains(s.Id))
            .ToListAsync(ct);

        if (suggestions.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var accepted = 0;
        foreach (var suggestion in suggestions)
        {
            if (await TryApplyAcceptAsync(userId, suggestion, now, ct))
            {
                accepted++;
            }
        }

        if (accepted > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return accepted;
    }

    /// <summary>
    /// Applies one accept in-memory (no SaveChanges): parses the payload categoryId, loads the
    /// UserId-scoped transaction, sets CategoryId + CategorySource=AiSuggested + CategoryConfidence,
    /// and transitions the suggestion to Accepted. Returns false (no changes staged) when the payload
    /// is unusable or the transaction can't be resolved.
    /// </summary>
    private async Task<bool> TryApplyAcceptAsync(Guid userId, AiSuggestion suggestion, DateTime now, CancellationToken ct)
    {
        var categoryId = TryParseCategoryId(suggestion.SuggestedPayload);
        if (categoryId is null || suggestion.TransactionId is null)
        {
            return false;
        }

        var txn = await db.Transactions
            .FirstOrDefaultAsync(t => t.Id == suggestion.TransactionId.Value && t.UserId == userId, ct);
        if (txn is null)
        {
            return false;
        }

        // Apply as AiSuggested (NOT AiAuto): a user-confirmed label the AI never re-touches and the
        // kNN voter trusts as a positive example.
        txn.CategoryId = categoryId;
        txn.CategorySource = CategorySource.AiSuggested;
        txn.CategoryConfidence = suggestion.Confidence;
        txn.UpdatedAt = now;

        suggestion.Status = AiSuggestionStatus.Accepted;
        suggestion.DecidedAt = now;
        suggestion.UpdatedAt = now;

        return true;
    }

    /// <summary>
    /// Parses <c>{"categoryId":"&lt;guid&gt;"}</c> out of the jsonb payload. Returns null for a
    /// missing/empty/malformed payload rather than throwing — a corrupt payload just can't be applied.
    /// </summary>
    private static Guid? TryParseCategoryId(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("categoryId", out var prop)
                && prop.ValueKind == JsonValueKind.String
                && Guid.TryParse(prop.GetString(), out var id))
            {
                return id;
            }
        }
        catch (JsonException)
        {
            // Corrupt payload — treat as unresolvable.
        }

        return null;
    }
}
