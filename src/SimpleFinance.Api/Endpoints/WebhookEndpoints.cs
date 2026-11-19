using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Pluggy;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// SYNC-01's public webhook receiver -- a durable-queue endpoint per ARCHITECTURE.md Pattern 3:
/// validate (constant-time secret header, never HMAC -- no such scheme exists per
/// 02-RESEARCH.md), dedup by Pluggy's eventId, persist the raw WebhookEvent, enqueue a Hangfire
/// job, and return 2XX in well under Pluggy's 5-second retry-window budget. NO step in this
/// handler calls IFinancialDataProvider or ISyncReconciler directly -- all real reconciliation
/// work happens off the request thread in WebhookProcessorJob (Infrastructure.Jobs).
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webhooks");

        group.MapPost("/pluggy", ReceivePluggyWebhookAsync)
            .AllowAnonymous()
            .RequireRateLimiting("webhook");
    }

    private static async Task<IResult> ReceivePluggyWebhookAsync(
        HttpRequest request,
        SimpleFinanceDbContext db,
        IOptions<PluggyOptions> pluggyOptions,
        IBackgroundJobClient backgroundJobClient,
        CancellationToken ct)
    {
        var options = pluggyOptions.Value;

        // T-02-12: missing header or mismatch -> 401, nothing persisted. Never ==/string.Equals
        // on secret material -- CryptographicOperations.FixedTimeEquals is constant-time.
        if (!IsAuthenticSecretHeader(request, options))
        {
            return Results.Unauthorized();
        }

        // Read the raw body ONCE -- reused verbatim both for JSON parsing below and as the
        // persisted WebhookEvent.PayloadJson.
        string rawBody;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8))
        {
            rawBody = await reader.ReadToEndAsync(ct);
        }

        string eventId;
        string eventType;
        string? itemId = null;

        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;

            if (!root.TryGetProperty("eventId", out var eventIdProperty)
                || eventIdProperty.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(eventIdProperty.GetString()))
            {
                // Malformed payload -- should never happen from real Pluggy traffic, but never
                // let a malformed body crash the handler.
                return Results.BadRequest();
            }

            eventId = eventIdProperty.GetString()!;

            eventType = root.TryGetProperty("event", out var eventProperty)
                && eventProperty.ValueKind == JsonValueKind.String
                ? eventProperty.GetString() ?? string.Empty
                : string.Empty;

            // Optional/nullable -- not every event type carries itemId consistently
            // (02-RESEARCH.md).
            if (root.TryGetProperty("itemId", out var itemIdProperty) && itemIdProperty.ValueKind == JsonValueKind.String)
            {
                itemId = itemIdProperty.GetString();
            }
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        // T-02-13: idempotent no-op on replay -- still 2XX so Pluggy stops retrying
        // (ARCHITECTURE.md Pattern 3), but no second row and no second enqueue.
        if (await db.WebhookEvents.AnyAsync(e => e.EventId == eventId, ct))
        {
            return Results.Ok();
        }

        var now = DateTime.UtcNow;
        var webhookEvent = new WebhookEvent
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            EventType = eventType,
            PluggyItemId = itemId,
            PayloadJson = rawBody,
            ReceivedAt = now,
            Status = WebhookEventStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.WebhookEvents.Add(webhookEvent);
        await db.SaveChangesAsync(ct);

        // Detached from the HTTP request's lifetime -- CancellationToken.None is the Hangfire
        // convention here, matching IWebhookProcessor's own contract comment.
        backgroundJobClient.Enqueue<IWebhookProcessor>(p => p.ProcessAsync(webhookEvent.Id, CancellationToken.None));

        return Results.Ok();
    }

    private static bool IsAuthenticSecretHeader(HttpRequest request, PluggyOptions options)
    {
        if (!request.Headers.TryGetValue(options.WebhookSecretHeaderName, out var headerValues))
        {
            return false;
        }

        var presented = headerValues.ToString();
        var expected = options.WebhookSecretValue ?? string.Empty;

        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        // Returns false immediately (still constant w.r.t. the SECRET content) when lengths
        // differ -- length itself is not sensitive, per CryptographicOperations' own contract.
        return CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }
}
