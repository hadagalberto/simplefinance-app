using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Ai;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Ai;
using SimpleFinance.Infrastructure.Ai.Assistant;
using SimpleFinance.Infrastructure.Ai.Insights;
using SimpleFinance.Infrastructure.Jobs;
using SimpleFinance.Infrastructure.Notifications;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Reporting;
using ForecastEntity = SimpleFinance.Domain.Entities.Forecast;
using ForecastService = SimpleFinance.Infrastructure.Ai.Forecast.ForecastService;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// AI-FND-07/08: GET/PUT /api/ai/settings + GET /api/ai/usage — thin HTTP wrappers over
/// AiSettingsService (settings get-or-create + key-present projection) and AiUsageQueries (Dapper
/// month rollup). No business logic lives here. Mirrors PreferencesEndpoints.cs's exact structure
/// (group + RequireAuthorization + the verbatim GetUserId helper). All three routes are JWT-auth and
/// UserId-scoped (T-15-15); the settings response never carries an API-key value (T-15-14).
/// </summary>
public static class AiEndpoints
{
    public static void MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ai").RequireAuthorization();
        group.MapGet("settings", GetSettingsAsync);
        group.MapPut("settings", UpdateSettingsAsync);
        group.MapGet("usage", GetUsageAsync);

        // AI-CAT-05/06: the suggestion review queue + on-demand backlog run.
        group.MapGet("suggestions", GetSuggestionsAsync);
        group.MapPost("suggestions/{id:guid}/accept", AcceptSuggestionAsync);
        group.MapPost("suggestions/{id:guid}/reject", RejectSuggestionAsync);
        group.MapPost("suggestions/bulk-accept", BulkAcceptSuggestionsAsync);
        group.MapPost("categorize/run", RunCategorizationAsync);

        // AI-INS-04: the proactive insight/anomaly card strip. GET lists the caller's insights of a
        // given status (default New); POST dismiss retires one (IDOR-safe — a foreign/missing id ⇒ 404).
        group.MapGet("insights", GetInsightsAsync);
        group.MapPost("insights/{id:guid}/dismiss", DismissInsightAsync);

        // AI-FCT-02: the current-month (or ?month=YYYY-MM) forecast for the caller. UserId-scoped via the
        // service — a foreign/missing month ⇒ 404, no leak (T-19-04). The projected numbers + range +
        // drivers come from the persisted row (never recomputed here); Narrative is untrusted display prose.
        group.MapGet("forecast", GetForecastAsync);

        // AI-FCT-04: the monthly-summary read + resend surface. GET returns the caller's MonthlySummary
        // insight for ?month=YYYY-MM (default current UTC month); resend re-dispatches the STORED
        // Title/Body via NotificationDispatcher with NO regeneration and NO LLM call (T-19-13). Both
        // UserId-scoped — a foreign/missing month or id ⇒ 404, no leak (T-19-12).
        group.MapGet("summary", GetSummaryAsync);
        group.MapPost("summary/{id:guid}/resend", ResendSummaryAsync);

        // AI-CHAT-01: the streaming chat endpoint (native .NET 10 SSE over the orchestrator's event
        // stream) + conversation CRUD. All stay on this JWT-authorized, UserId-scoped group.
        group.MapPost("chat", PostChatAsync);
        group.MapGet("conversations", ListConversationsAsync);
        group.MapGet("conversations/{id:guid}", GetConversationAsync);
        group.MapPatch("conversations/{id:guid}", RenameConversationAsync);
        group.MapDelete("conversations/{id:guid}", DeleteConversationAsync);

        // AI-CHAT-04: the ONLY paths that execute a proposed write. Confirm dispatches by ActionType to
        // the matching domain service exactly once (idempotent, expiry-guarded); cancel discards the
        // proposal. Both UserId-scoped — a foreign/missing id ⇒ 404, a non-Pending action ⇒ 409.
        group.MapPost("actions/{id:guid}/confirm", ConfirmActionAsync);
        group.MapPost("actions/{id:guid}/cancel", CancelActionAsync);
    }

    // AI-CHAT-04: confirm and EXECUTE a staged AiPendingAction exactly once. 200 { status:"Confirmed" }
    // on success; 404 for a missing/foreign id (IDOR-safe, T-17-20); 409 for a non-Pending or expired
    // action (idempotency + stale-proposal guards, T-17-19/T-17-22) — no double-write.
    private static async Task<IResult> ConfirmActionAsync(
        Guid id,
        AiPendingActionService actionService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var outcome = await actionService.ConfirmAsync(userId.Value, id, ct);
        return outcome switch
        {
            PendingActionOutcome.Success => Results.Ok(new PendingActionResponse("Confirmed")),
            PendingActionOutcome.Conflict => Results.Conflict(new PendingActionResponse("Conflict")),
            _ => Results.NotFound(),
        };
    }

    // AI-CHAT-04: cancel a staged AiPendingAction (no domain write). 200 { status:"Cancelled" }; 404 for
    // a missing/foreign id; 409 for a non-Pending action.
    private static async Task<IResult> CancelActionAsync(
        Guid id,
        AiPendingActionService actionService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var outcome = await actionService.CancelAsync(userId.Value, id, ct);
        return outcome switch
        {
            PendingActionOutcome.Success => Results.Ok(new PendingActionResponse("Cancelled")),
            PendingActionOutcome.Conflict => Results.Conflict(new PendingActionResponse("Conflict")),
            _ => Results.NotFound(),
        };
    }

    // AI-CHAT-01: stream one assistant turn as Server-Sent Events. Returns
    // TypedResults.ServerSentEvents over AssistantOrchestrator.RunAsync — the framework writes the
    // event:/data: framing and content-type; each SseItem carries its own eventType
    // (token/tool/action/done/error) so the browser switches on the event line. The Minimal API
    // CancellationToken binds to HttpContext.RequestAborted, so a client disconnect cancels the loop
    // and the in-flight LLM stream (T-17-16). A provided-but-foreign conversation id is a 404 BEFORE
    // the orchestrator runs (the orchestrator would otherwise silently start a fresh conversation for
    // an unresolved id) — preserving IDOR-safe ownership semantics (T-17-15).
    private static async Task<IResult> PostChatAsync(
        ChatRequest request,
        IValidator<ChatRequest> validator,
        AssistantOrchestrator orchestrator,
        ConversationService conversations,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        if (request.ConversationId is Guid conversationId)
        {
            var existing = await conversations.GetWithMessagesAsync(userId.Value, conversationId, ct);
            if (existing is null)
            {
                return Results.NotFound();
            }
        }

        var stream = orchestrator.RunAsync(userId.Value, request.ConversationId, request.Message, ct);

        // NOTE: pass the SseItem<ChatEvent> stream WITHOUT a top-level eventType. Supplying one binds
        // overload resolution to ServerSentEvents<T>(IAsyncEnumerable<T>, string) with T = SseItem<ChatEvent>,
        // which DOUBLE-WRAPS each item (every frame becomes `event: message` with the serialized outer
        // SseItem as data). The no-eventType overload consumes the SseItem<ChatEvent> directly, so each
        // item's own eventType (token/tool/action/done/error) is written as the SSE `event:` line and the
        // ChatEvent is the `data:` payload — the exact wire contract the client (17-07) switches on.
        return TypedResults.ServerSentEvents(stream);
    }

    // AI-CHAT-01: the caller's conversations, most-recent activity first (UserId-scoped via the service).
    private static async Task<IResult> ListConversationsAsync(
        ConversationService conversations,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var rows = await conversations.ListAsync(userId.Value, ct);
        var summaries = rows
            .Select(c => new ConversationSummary(c.Id, c.Title, c.CreatedAt, c.UpdatedAt))
            .ToList();

        return Results.Ok(summaries);
    }

    // AI-CHAT-01: one conversation with its ordered message thread. A missing/foreign id ⇒ 404, no leak.
    private static async Task<IResult> GetConversationAsync(
        Guid id,
        ConversationService conversations,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await conversations.GetWithMessagesAsync(userId.Value, id, ct);
        if (result is null)
        {
            return Results.NotFound();
        }

        var messages = result.Messages
            .Select(m => new ChatMessageDto(m.Id, m.Role.ToString(), m.Content, m.ToolName, m.CreatedAt))
            .ToList();

        return Results.Ok(new ConversationDetail(
            result.Conversation.Id, result.Conversation.Title,
            result.Conversation.CreatedAt, result.Conversation.UpdatedAt, messages));
    }

    // AI-CHAT-01: rename a conversation. Manual FluentValidation (non-empty, ≤200 chars). A
    // missing/foreign id ⇒ 404 with no mutation (T-17-15).
    private static async Task<IResult> RenameConversationAsync(
        Guid id,
        RenameConversationRequest request,
        IValidator<RenameConversationRequest> validator,
        ConversationService conversations,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var renamed = await conversations.RenameAsync(userId.Value, id, request.Title, ct);
        return renamed ? Results.NoContent() : Results.NotFound();
    }

    // AI-CHAT-01: soft-delete a conversation. A missing/foreign id ⇒ 404 with no mutation (T-17-15).
    private static async Task<IResult> DeleteConversationAsync(
        Guid id,
        ConversationService conversations,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var deleted = await conversations.DeleteAsync(userId.Value, id, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    // AI-CAT-06: the caller's pending suggestion queue. `?status=pending` is the only supported
    // (and default) view this phase — accepted/rejected suggestions leave the queue.
    private static async Task<IResult> GetSuggestionsAsync(
        string? status,
        AiSuggestionService suggestionService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var suggestions = await suggestionService.GetPendingAsync(userId.Value, ct);
        return Results.Ok(suggestions);
    }

    // AI-CAT-06 (T-16-12): accept applies the suggested category as AiSuggested + marks the suggestion
    // Accepted. A missing/foreign/non-pending id ⇒ 404 with no mutation.
    private static async Task<IResult> AcceptSuggestionAsync(
        Guid id,
        AiSuggestionService suggestionService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var accepted = await suggestionService.AcceptAsync(userId.Value, id, ct);
        return accepted ? Results.NoContent() : Results.NotFound();
    }

    // AI-CAT-06: reject marks the suggestion Rejected; the transaction stays "Sem categoria". A
    // missing/foreign/non-pending id ⇒ 404.
    private static async Task<IResult> RejectSuggestionAsync(
        Guid id,
        AiSuggestionService suggestionService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var rejected = await suggestionService.RejectAsync(userId.Value, id, ct);
        return rejected ? Results.NoContent() : Results.NotFound();
    }

    // AI-CAT-06 (T-16-13): accept a batch of pending suggestions. Manual FluentValidation (non-empty,
    // capped, no empty guids) ⇒ 400 on a bad payload; returns the count actually accepted.
    private static async Task<IResult> BulkAcceptSuggestionsAsync(
        BulkAcceptSuggestionsCommand command,
        IValidator<BulkAcceptSuggestionsCommand> validator,
        AiSuggestionService suggestionService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var accepted = await suggestionService.BulkAcceptAsync(userId.Value, command.Ids, ct);
        return Results.Ok(new BulkAcceptResponse(accepted));
    }

    // AI-CAT-05: enqueue the backlog categorization job on demand. Returns 202 Accepted with the
    // Hangfire job id (a NoOp/fake client is used under the Testing environment).
    private static IResult RunCategorizationAsync(
        IBackgroundJobClient backgroundJobClient,
        ClaimsPrincipal principal)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var jobId = backgroundJobClient.Enqueue<AiCategorizationJob>(job => job.ExecuteAsync(CancellationToken.None));
        return Results.Accepted($"/api/ai/categorize/run", new AiCategorizeRunResponse(jobId, true));
    }

    // AI-INS-04: the caller's insights/anomalies of the given status (?status=new|seen|dismissed; default
    // New). UserId-scoped via the service — never returns another user's rows (T-18-18). Enum fields are
    // projected as strings so the frontend switches on stable names; DataJson passes through as the
    // authoritative jsonb figures. Body is untrusted display prose (the client escapes it — T-18-19).
    private static async Task<IResult> GetInsightsAsync(
        string? status,
        AiInsightService insightService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var parsedStatus = ParseInsightStatus(status);
        var insights = await insightService.GetAsync(userId.Value, parsedStatus, ct);
        var response = insights.Select(ToInsightResponse).ToList();
        return Results.Ok(response);
    }

    // AI-INS-04 (T-18-18): dismiss a caller-owned insight (Status→Dismissed). A missing/foreign id ⇒ 404
    // with no mutation, exactly like RejectSuggestionAsync — a dismissed insight never reappears (same
    // (UserId, DedupKey) existence gate blocks the job from re-creating it).
    private static async Task<IResult> DismissInsightAsync(
        Guid id,
        AiInsightService insightService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var dismissed = await insightService.DismissAsync(userId.Value, id, ct);
        return dismissed ? Results.NoContent() : Results.NotFound();
    }

    // AI-FCT-02: the caller's forecast for ?month=YYYY-MM (default current month). UserId-scoped via the
    // service (T-19-04) — a foreign/missing month ⇒ 404. The projected numbers + range + drivers come
    // straight from the persisted row (never recomputed); ByCategoryJson passes through as the authoritative
    // jsonb figures; Narrative is untrusted display prose (the client escapes it).
    private static async Task<IResult> GetForecastAsync(
        string? month,
        ForecastService forecastService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (!TryResolveMonth(month, out var monthStartUtc, out _))
        {
            return Results.BadRequest(new { error = "month must be in YYYY-MM format." });
        }

        var forecast = await forecastService.GetAsync(userId.Value, monthStartUtc, ct);
        if (forecast is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(ToForecastResponse(forecast));
    }

    // Projects a Forecast row to its wire record — numbers from the persisted row, ByCategoryJson passed
    // through as the authoritative jsonb, Narrative left as-is (untrusted display prose the client escapes).
    private static AiForecastResponse ToForecastResponse(ForecastEntity f) => new(
        f.Month,
        f.ProjectedIncome,
        f.ProjectedExpense,
        f.ProjectedBalance,
        f.BalanceLow,
        f.BalanceHigh,
        f.ByCategoryJson,
        f.Method,
        f.Narrative,
        f.CreatedAt);

    // AI-FCT-04: the caller's MonthlySummary insight for ?month=YYYY-MM (default current UTC month).
    // UserId-scoped via GetByKindPeriodAsync (T-19-12) — a foreign/missing month ⇒ 404, no leak. The
    // figures come from the authoritative DataJson blob (the client parses it, never the prose); Title/Body
    // are the reasoner prose (or deterministic fallback) — untrusted display prose the client escapes.
    private static async Task<IResult> GetSummaryAsync(
        string? month,
        AiInsightService insightService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (!TryResolveMonth(month, out var monthStartUtc, out _))
        {
            return Results.BadRequest(new { error = "month must be in YYYY-MM format." });
        }

        var summary = await insightService.GetByKindPeriodAsync(
            userId.Value, AiInsightKind.MonthlySummary, monthStartUtc, ct);
        if (summary is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(ToSummaryResponse(summary));
    }

    // AI-FCT-04 (T-19-13): re-dispatch a caller-owned MonthlySummary via NotificationDispatcher using the
    // STORED Title/Body only — NO summarizer call, NO new AiInsight, so it can never incur LLM cost or
    // overwrite the month's summary. UserId-scoped load (Id + UserId + Kind=MonthlySummary); a
    // foreign/missing id ⇒ 404 with no dispatch (IDOR-safe, mirrors DismissInsightAsync — T-19-12).
    private static async Task<IResult> ResendSummaryAsync(
        Guid id,
        SimpleFinanceDbContext db,
        NotificationDispatcher dispatcher,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var summary = await db.AiInsights.FirstOrDefaultAsync(
            i => i.Id == id && i.UserId == userId.Value && i.Kind == AiInsightKind.MonthlySummary, ct);
        if (summary is null)
        {
            return Results.NotFound();
        }

        var user = await db.Users.FirstAsync(u => u.Id == userId.Value, ct);
        await dispatcher.DispatchAsync(
            NotificationChannelType.Both, summary.Title, summary.Body, user.Email, ct);

        return Results.NoContent();
    }

    // Projects a MonthlySummary AiInsight to its wire record — DataJson passed through as the authoritative
    // jsonb figures, Title/Body left as-is (untrusted display prose the client escapes).
    private static AiSummaryResponse ToSummaryResponse(AiInsight insight) => new(
        insight.Id,
        insight.Title,
        insight.Body,
        insight.DataJson,
        insight.PeriodStart,
        insight.PeriodEnd,
        insight.CreatedAt);

    // Parses ?status=new|seen|dismissed (case-insensitive) into AiInsightStatus. Absent/blank/unknown ⇒
    // New (the default card-strip view — the New→Seen→Dismissed lifecycle keeps New the live queue).
    private static AiInsightStatus ParseInsightStatus(string? status)
    {
        return Enum.TryParse<AiInsightStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : AiInsightStatus.New;
    }

    // Projects an AiInsight to its wire record — enums as stable string names, DataJson passed through.
    private static AiInsightResponse ToInsightResponse(AiInsight insight) => new(
        insight.Id,
        insight.Kind.ToString(),
        insight.Severity.ToString(),
        insight.Title,
        insight.Body,
        insight.PeriodStart,
        insight.PeriodEnd,
        insight.DataJson,
        insight.Status.ToString(),
        insight.CreatedAt);

    private static async Task<IResult> GetSettingsAsync(
        AiSettingsService settingsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await settingsService.GetAsync(userId.Value, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> UpdateSettingsAsync(
        UpdateAiSettingsCommand command,
        IValidator<UpdateAiSettingsCommand> validator,
        AiSettingsService settingsService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // Manual FluentValidation invocation — a failing result throws ValidationException, which
        // ValidationExceptionMiddleware maps to a 400 validation problem (never the deprecated
        // FluentValidation.AspNetCore auto-pipeline).
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var result = await settingsService.UpdateAsync(userId.Value, command, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetUsageAsync(
        string? month,
        AiSettingsService settingsService,
        AiUsageQueries usageQueries,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (!TryResolveMonth(month, out var monthStartUtc, out var monthLabel))
        {
            return Results.BadRequest(new { error = "month must be in YYYY-MM format." });
        }

        var nextMonthStartUtc = monthStartUtc.AddMonths(1);

        var settings = await settingsService.GetAsync(userId.Value, ct);
        var features = await usageQueries.GetMonthlySpendByFeatureAsync(userId.Value, monthStartUtc, nextMonthStartUtc, ct);

        var featureResponses = features
            .Select(f => new AiUsageFeatureResponse(
                f.Feature.ToString(), f.PromptTokens, f.CompletionTokens, f.EstimatedCostUsd))
            .ToList();

        var totalCostUsd = features.Sum(f => f.EstimatedCostUsd);

        return Results.Ok(new AiUsageResponse(
            monthLabel, totalCostUsd, settings.AiMonthlyBudgetUsd, featureResponses));
    }

    // Resolves the optional ?month=YYYY-MM into a first-of-month UTC instant. Absent/blank => the
    // current UTC calendar month. Returns false only for a present-but-malformed value.
    private static bool TryResolveMonth(string? month, out DateTime monthStartUtc, out string monthLabel)
    {
        if (string.IsNullOrWhiteSpace(month))
        {
            var now = DateTime.UtcNow;
            monthStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            monthLabel = monthStartUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return true;
        }

        if (DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            monthStartUtc = new DateTime(parsed.Year, parsed.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            monthLabel = monthStartUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return true;
        }

        monthStartUtc = default;
        monthLabel = string.Empty;
        return false;
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}

/// <summary>One feature's month-to-date AI spend for the usage panel (GET /api/ai/usage).</summary>
public sealed record AiUsageFeatureResponse(
    string Feature, long PromptTokens, long CompletionTokens, decimal EstimatedCostUsd);

/// <summary>GET /api/ai/usage response: the resolved month, total spend, the cap, and per-feature rows.</summary>
public sealed record AiUsageResponse(
    string Month, decimal TotalCostUsd, decimal MonthlyBudgetUsd, IReadOnlyList<AiUsageFeatureResponse> Features);

/// <summary>POST /api/ai/suggestions/bulk-accept response: how many suggestions were actually accepted.</summary>
public sealed record BulkAcceptResponse(int Accepted);

/// <summary>POST /api/ai/categorize/run response: the enqueued Hangfire job id + enqueued flag.</summary>
public sealed record AiCategorizeRunResponse(string JobId, bool Enqueued);

/// <summary>
/// POST /api/ai/actions/{id}/confirm|cancel response: the resulting action status
/// (<c>Confirmed</c> | <c>Cancelled</c> | <c>Conflict</c>). The 17-07 client switches on this.
/// </summary>
public sealed record PendingActionResponse(string Status);

/// <summary>
/// GET /api/ai/insights response row (AI-INS-04). <c>Kind</c>/<c>Severity</c>/<c>Status</c> are the enum
/// string names the card strip switches on (Anomaly→AnomalyCard, else InsightCard; Severity→badge color);
/// <c>DataJson</c> is the authoritative jsonb figures blob (the client parses it, never the prose);
/// <c>Body</c> is untrusted display prose (the client escapes it — T-18-19).
/// </summary>
public sealed record AiInsightResponse(
    Guid Id,
    string Kind,
    string Severity,
    string Title,
    string Body,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    string DataJson,
    string Status,
    DateTime CreatedAt);

/// <summary>
/// GET /api/ai/forecast response (AI-FCT-02). The projected numbers + range are the deterministic figures
/// from the persisted <c>Forecast</c> row (never recomputed at read time); <c>ByCategoryJson</c> is the
/// authoritative per-category driver jsonb blob (the client parses it, never the prose); <c>Narrative</c> is
/// the optional, nullable LLM explanation (untrusted display prose — the client escapes it; null when the
/// budget cap was hit at generation time).
/// </summary>
public sealed record AiForecastResponse(
    DateTime Month,
    decimal ProjectedIncome,
    decimal ProjectedExpense,
    decimal ProjectedBalance,
    decimal BalanceLow,
    decimal BalanceHigh,
    string ByCategoryJson,
    string Method,
    string? Narrative,
    DateTime CreatedAt);

/// <summary>
/// GET /api/ai/summary response (AI-FCT-04). The caller's MonthlySummary for the resolved month.
/// <c>DataJson</c> is the authoritative fact-package jsonb blob (the client parses it, never the prose);
/// <c>Title</c>/<c>Body</c> are the reasoner prose (or deterministic fallback) — untrusted display prose
/// the client escapes. <c>PeriodStart</c> is the 1st-of-prior-month UTC the summary covers.
/// </summary>
public sealed record AiSummaryResponse(
    Guid Id,
    string Title,
    string Body,
    string DataJson,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime CreatedAt);
