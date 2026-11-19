using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using SimpleFinance.Infrastructure.Backup;

namespace SimpleFinance.Api.Endpoints;

/// <summary>
/// CFG-03: GET /api/backup/export -- a thin HTTP wrapper over BackupService, which owns every
/// bit of actual business logic (the scoped multi-table query + serialization). Mirrors
/// ExportEndpoints.cs's exact structure (group + RequireAuthorization + GetUserId helper), plus
/// T-08-14's dedicated "export" rate-limit policy -- a full-data-dump endpoint is a natural
/// DoS/scrape target even for a single-user app.
/// </summary>
public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backup").RequireAuthorization().RequireRateLimiting("export");
        group.MapGet("/export", ExportBackupAsync);
    }

    private static async Task<IResult> ExportBackupAsync(
        string? format,
        BackupService backupService,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // Only "json" is accepted for now (08-CONTEXT.md's discretion to pick the simpler
        // robust option over a per-table CSV zip).
        if (!string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "format must be json." });
        }

        var json = await backupService.BuildJsonDumpAsync(userId.Value, ct);

        return Results.File(
            Encoding.UTF8.GetBytes(json),
            "application/json",
            $"simplefinance-backup-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    private static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return subClaim is not null && Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}
