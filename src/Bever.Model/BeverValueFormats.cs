using System.Globalization;
using System.Text.RegularExpressions;

namespace Bever.Model;

/// <summary>
/// The text formats of Bever primitive values in JSON. The compiler, migrations, and runtimes share these rules.
/// </summary>
public static partial class BeverValueFormats
{
    /// <summary>Whether <paramref name="value"/> is a valid JSON string value for primitive type <paramref name="typeName"/>.</summary>
    public static bool IsValid(string typeName, string value) => typeName switch
    {
        "Text" => true,
        "Email" => value.Length <= 254 && EmailPattern().IsMatch(value),
        "Phone" => value.Length <= 32 && PhonePattern().IsMatch(value) && value.Count(char.IsAsciiDigit) >= 3,
        "Url" => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0,
        "Date" => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "Time" => TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "DateTime" => DateTimePattern().IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        _ => false
    };

    /// <summary>The canonical stored form of a valid value: date-times become UTC with millisecond precision.</summary>
    public static string Normalize(string typeName, string value) => typeName switch
    {
        "DateTime" => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        "Email" => value.Trim(),
        _ => value
    };

    /// <summary>A short description of the expected format, for validation messages.</summary>
    public static string Describe(string typeName) => typeName switch
    {
        "Email" => "Enter a valid email address.",
        "Phone" => "Enter a valid phone number.",
        "Url" => "Enter a full web address starting with http:// or https://.",
        "Date" => "Enter a date as YYYY-MM-DD.",
        "Time" => "Enter a time as HH:MM.",
        "DateTime" => "Enter a date and time with a time zone, for example 2026-01-31T09:00:00Z.",
        _ => "Enter a valid value."
    };

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^\+?[0-9 ().\-]+$")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex DateTimePattern();
}
