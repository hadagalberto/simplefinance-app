using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleFinance.Application.Connections.ConnectToken;
using SimpleFinance.Application.Connections.Register;
using SimpleFinance.Application.Connections.Remove;
using SimpleFinance.Application.Sync;
using SimpleFinance.Domain.Entities;
using SimpleFinance.Domain.Enums;
using SimpleFinance.Infrastructure.Persistence;
using SimpleFinance.Infrastructure.Pluggy;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// PLG-02..06: the user-facing connections lifecycle surface -- mint Connect Tokens (new +
/// update-mode reconnect), register a Connection from the widget's onSuccess(itemId) callback and
/// trigger a first sync via the shared ISyncReconciler, list connections with a suggestedAction
/// per row (ItemStatusMapper), and remove a connection (Pluggy DELETE + cascading soft-delete).
/// Single-user app (T-02-19) -- every route requires only "authenticated at all", matching
/// SyncEndpoints/AuthEndpoints' existing JWT bearer scheme; no per-user filtering beyond what the
/// DbContext's own soft-delete query filter already applies.
/// </summary>
public static class ConnectionsEndpoints
{
    public static void MapConnectionsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/pluggy/connect-token", CreateConnectTokenAsync).RequireAuthorization();

        var group = app.MapGroup("/api/connections").RequireAuthorization();
        group.MapGet("", ListConnectionsAsync);
        group.MapPost("", RegisterConnectionAsync);
        group.MapPatch("/{id:guid}", RenameConnectionAsync);
        group.MapPost("/{id:guid}/reconnect-token", CreateReconnectTokenAsync);
        group.MapDelete("/{id:guid}", RemoveConnectionAsync);
    }

    private static async Task<IResult> CreateConnectTokenAsync(
        CreateConnectTokenCommand command,
        IValidator<CreateConnectTokenCommand> validator,
        IFinancialDataProvider provider,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(command, ct);

        // PLG-02: never cached/reused -- a fresh token is minted on every call.
        var token = await provider.CreateConnectTokenAsync(command.ExistingPluggyItemId, ct);
        return Results.Ok(new { connectToken = token });
    }

    private static async Task<IResult> ListConnectionsAsync(
        SimpleFinanceDbContext db,
        CancellationToken ct)
    {
        // Global soft-delete query filter already excludes removed connections.
        var connections = await db.Connections.ToListAsync(ct);

        var response = connections.Select(ToResponse).ToList();
        return Results.Ok(response);
    }

    private static async Task<IResult> RenameConnectionAsync(
        Guid id,
        RenameConnectionCommand command,
        SimpleFinanceDbContext db,
        CancellationToken ct)
    {
        var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (connection is null)
        {
            return Results.NotFound();
        }

        // Trim; empty/whitespace clears the custom name (falls back to institution name).
        var name = command.CustomName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            connection.CustomName = null;
        }
        else if (name.Length > 100)
        {
            return Results.Json(new { error = "custom_name_too_long" }, statusCode: StatusCodes.Status400BadRequest);
        }
        else
        {
            connection.CustomName = name;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(connection));
    }

    private sealed record RenameConnectionCommand(string? CustomName);

    private static async Task<IResult> RegisterConnectionAsync(
        RegisterConnectionCommand command,
        IValidator<RegisterConnectionCommand> validator,
        SimpleFinanceDbContext db,
        IFinancialDataProvider provider,
        IBackgroundJobClient backgroundJobClient,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(command, ct);

        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // IgnoreQueryFilters so a previously-removed-then-reconnected-with-the-same-itemId edge
        // case is still caught (T-02-21) -- the soft-delete filter alone would silently miss it.
        var existing = await db.Connections
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.PluggyItemId == command.PluggyItemId, ct);

        Connection connection;
        var now = DateTime.UtcNow;

        if (existing is not null)
        {
            if (existing.DeletedAt is null)
            {
                // True duplicate -- never a second row for the same itemId.
                return DuplicateConnection();
            }

            // Legitimate reconnect-after-remove: transparently "undelete" rather than erroring.
            existing.DeletedAt = null;
            connection = existing;
        }
        else
        {
            connection = new Connection
            {
                Id = Guid.NewGuid(),
                UserId = userId.Value,
                PluggyItemId = command.PluggyItemId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Connections.Add(connection);
        }

        // Fetch the real current status so the row is created with actual Status/ExecutionStatus
        // rather than a placeholder.
        var itemStatus = await provider.GetItemStatusAsync(command.PluggyItemId, ct);
        ApplyItemStatus(connection, itemStatus);

        await db.SaveChangesAsync(ct);

        // PRD's 3-value TriggeredBy enum has no dedicated "initial connect" value -- Manual is
        // the correct fit for the first sync fired on connect (mirrors SyncEndpoints' own
        // Manual-trigger enqueue pattern). Enqueued via Hangfire, never called synchronously --
        // a live Pluggy round-trip must never block this POST's response.
        backgroundJobClient.Enqueue<ISyncReconciler>(
            r => r.SyncConnectionAsync(connection.Id, SyncTrigger.Manual, CancellationToken.None));

        return Results.Created($"/api/connections/{connection.Id}", ToResponse(connection));
    }

    private static async Task<IResult> CreateReconnectTokenAsync(
        Guid id,
        SimpleFinanceDbContext db,
        IFinancialDataProvider provider,
        CancellationToken ct)
    {
        // Global soft-delete query filter already excludes removed connections -- a missing/
        // soft-deleted id both resolve to null here.
        var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (connection is null)
        {
            return Results.NotFound();
        }

        // Same provider method as the create-new path (Task 1), but now scoped to the existing
        // itemId -- this is what puts the Pluggy widget into update/reconnect mode per
        // RESEARCH.md's MFA Challenge/Response Flow section.
        var token = await provider.CreateConnectTokenAsync(connection.PluggyItemId, ct);
        return Results.Ok(new { connectToken = token });
    }

    private static async Task<IResult> RemoveConnectionAsync(
        Guid id,
        // [FromBody] is required here: ASP.NET Core's minimal-API body inference is disallowed
        // for DELETE (throws "Body was inferred but the method does not allow inferred body
        // parameters" at startup) -- this is the one HTTP verb where implicit complex-type body
        // binding is unsupported, unlike every other verb used elsewhere in this codebase.
        [FromBody] RemoveConnectionCommand command,
        IValidator<RemoveConnectionCommand> validator,
        SimpleFinanceDbContext db,
        IFinancialDataProvider provider,
        CancellationToken ct)
    {
        // T-02-20: validated before ever touching Pluggy or the database -- Confirm must equal
        // true (RemoveConnectionValidator) or ValidateAndThrowAsync surfaces a 400 via the
        // existing ValidationExceptionMiddleware, with zero side effects below this line.
        await validator.ValidateAndThrowAsync(command, ct);

        var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (connection is null)
        {
            return Results.NotFound();
        }

        await provider.DeleteItemAsync(connection.PluggyItemId, ct);

        var now = DateTime.UtcNow;
        connection.DeletedAt = now;
        connection.UpdatedAt = now;

        var accounts = await db.Accounts.Where(a => a.ConnectionId == id).ToListAsync(ct);
        var accountIds = accounts.Select(a => a.Id).ToList();

        foreach (var account in accounts)
        {
            account.DeletedAt = now;
            account.UpdatedAt = now;
        }

        var investments = await db.Investments.Where(i => i.ConnectionId == id).ToListAsync(ct);
        foreach (var investment in investments)
        {
            investment.DeletedAt = now;
            investment.UpdatedAt = now;
        }

        // Transaction doesn't carry ConnectionId directly -- join through the just-soft-deleted
        // Accounts.
        var transactions = await db.Transactions.Where(t => accountIds.Contains(t.AccountId)).ToListAsync(ct);
        foreach (var transaction in transactions)
        {
            transaction.DeletedAt = now;
            transaction.UpdatedAt = now;
        }

        // CreditCardDetail is AuditableEntity, not UserOwnedEntity (02-01's entity design) -- it
        // has no independent DeletedAt/soft-delete column. It carries no independent audit value
        // once its parent Account is gone, so hard-delete these specific rows for cleanliness
        // rather than leaving orphaned, never-soft-deletable rows behind.
        var creditCardDetails = await db.CreditCardDetails
            .Where(c => accountIds.Contains(c.AccountId))
            .ToListAsync(ct);
        db.CreditCardDetails.RemoveRange(creditCardDetails);

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static void ApplyItemStatus(Connection connection, ProviderItemStatus itemStatus)
    {
        connection.Status = ParseConnectionStatus(itemStatus.Status);
        connection.ExecutionStatus = itemStatus.ExecutionStatus;
        connection.StatusDetail = itemStatus.StatusDetail;
        connection.ErrorCode = itemStatus.ErrorCode;
        connection.ErrorMessage = itemStatus.ErrorMessage;
        connection.ConnectorId = itemStatus.ConnectorId;
        connection.ConnectorName = itemStatus.ConnectorName;
        connection.InstitutionName = itemStatus.InstitutionName;
        connection.ImageUrl = itemStatus.ImageUrl;
        connection.UpdatedAt = DateTime.UtcNow;
    }

    // Mirrors SyncConnectionUseCase.ParseConnectionStatus (private there, so duplicated here
    // rather than reaching into Infrastructure.Sync internals) -- both sites parse the identical
    // 5-value Pluggy status enum, never throwing on an unrecognized value.
    private static ConnectionStatus ParseConnectionStatus(string status) => status.ToUpperInvariant() switch
    {
        "UPDATING" => ConnectionStatus.Updating,
        "UPDATED" => ConnectionStatus.Updated,
        "LOGIN_ERROR" => ConnectionStatus.LoginError,
        "WAITING_USER_INPUT" => ConnectionStatus.WaitingUserInput,
        "OUTDATED" => ConnectionStatus.Outdated,
        _ => ConnectionStatus.Outdated,
    };

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }

    private static ConnectionResponse ToResponse(Connection c) => new(
        c.Id,
        c.CustomName,
        c.InstitutionName,
        c.ConnectorName,
        c.ImageUrl,
        c.Status.ToString(),
        c.ExecutionStatus,
        c.StatusDetail,
        c.ErrorCode,
        c.ErrorMessage,
        c.LastSyncAt,
        ItemStatusMapper.Map(c.Status, c.ExecutionStatus).ToString());

    private static IResult DuplicateConnection() =>
        Results.Json(new { error = "duplicate_connection" }, statusCode: StatusCodes.Status409Conflict);

    private sealed record ConnectionResponse(
        Guid Id,
        string? CustomName,
        string? InstitutionName,
        string? ConnectorName,
        string? ImageUrl,
        string Status,
        string? ExecutionStatus,
        string? StatusDetail,
        string? ErrorCode,
        string? ErrorMessage,
        DateTime? LastSyncAt,
        string SuggestedAction);
}
