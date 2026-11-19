using FluentValidation;

namespace SimpleFinance.Api.Middleware;

/// <summary>
/// Centralizes mapping a thrown FluentValidation.ValidationException to the module-wide
/// `{ errors: { field: [messages] } }` 400 JSON shape, so every Auth endpoint (and 01-05's
/// future /2fa/verify) shares one consistent validation-error contract instead of each endpoint
/// re-implementing the same grouping/casing logic. Endpoints opt in by calling
/// IValidator&lt;T&gt;.ValidateAndThrowAsync(...) instead of ValidateAsync(...) + manual mapping.
/// </summary>
public class ValidationExceptionMiddleware
{
    private readonly RequestDelegate _next;

    public ValidationExceptionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationException ex)
        {
            var errors = ex.Errors
                .GroupBy(e => ToCamelCase(e.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { errors });
        }
    }

    private static string ToCamelCase(string propertyName) =>
        string.IsNullOrEmpty(propertyName)
            ? propertyName
            : char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
}

public static class ValidationExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseValidationExceptionHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<ValidationExceptionMiddleware>();
}
