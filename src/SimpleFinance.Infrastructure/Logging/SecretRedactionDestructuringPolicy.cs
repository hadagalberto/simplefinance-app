using Serilog.Core;
using Serilog.Events;

namespace SimpleFinance.Infrastructure.Logging;

/// <summary>
/// FND-03 / PITFALLS.md #12 enforcement point: strips sensitive values out of structured log
/// events before any sink writes them. Inspects destructured object property names
/// case-insensitively for substrings that indicate a secret (password, secret, token, totp,
/// authorization, apikey) and replaces the value with a fixed redaction marker.
///
/// Applies to structured/destructured objects passed to Serilog (e.g. `Log.Information("{@Request}",
/// request)`). It intentionally does NOT enable ASP.NET Core's HTTP request/response body
/// logging middleware — see Program.cs — because that is a leak vector this policy cannot see
/// into (raw bytes, not structured log properties).
/// </summary>
public sealed class SecretRedactionDestructuringPolicy : IDestructuringPolicy
{
    private const string RedactedValue = "***REDACTED***";

    private static readonly string[] SensitiveSubstrings =
    [
        "password",
        "secret",
        "token",
        "totp",
        "authorization",
        "apikey",
    ];

    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        result = null!;

        if (value is null)
        {
            return false;
        }

        var type = value.GetType();

        // Only intercept plain objects with properties (POCOs/DTOs/records); let Serilog's
        // default policies handle primitives, collections, and everything else.
        if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
        {
            return false;
        }

        var properties = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (properties.Length == 0)
        {
            return false;
        }

        var logProperties = new List<LogEventProperty>();

        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length > 0)
            {
                // Skip indexers — they are not simple named properties.
                continue;
            }

            object? rawValue;
            try
            {
                rawValue = property.GetValue(value);
            }
            catch
            {
                // Property getter threw — skip rather than fail logging entirely.
                continue;
            }

            var propertyValue = IsSensitivePropertyName(property.Name)
                ? propertyValueFactory.CreatePropertyValue(RedactedValue)
                : propertyValueFactory.CreatePropertyValue(rawValue, destructureObjects: true);

            logProperties.Add(new LogEventProperty(property.Name, propertyValue));
        }

        result = new StructureValue(logProperties, type.Name);
        return true;
    }

    private static bool IsSensitivePropertyName(string propertyName)
    {
        foreach (var substring in SensitiveSubstrings)
        {
            if (propertyName.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
