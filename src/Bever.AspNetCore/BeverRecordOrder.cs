using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bever.AspNetCore;

// List order: the Read action's OrderBy, otherwise newest first. Ties fall back to newest first, then ID.
internal static class BeverRecordOrder
{
    public static Comparison<JsonObject> For(BeverActionContext read)
    {
        var orderBy = read.Model.OrderBy;
        var field = orderBy?.Path?.Segments.FirstOrDefault();
        var descending = orderBy?.Direction == "Desc";
        var type = read.Entity.Model.Fields.FirstOrDefault(candidate => candidate.Name == field)?.Type;
        var typeName = type?.Name;
        // Enums sort in declaration order (Low, Normal, High), not alphabetically.
        var options = type?.Kind == "Enum" ? type.Options.Select(option => option.Name).ToList() : null;
        var key = field is null ? null : JsonNamingPolicy.CamelCase.ConvertName(field);

        return (left, right) =>
        {
            if (key is not null)
            {
                var compared = options is null
                    ? CompareValues(left[key], right[key], typeName)
                    : CompareValues(Rank(left[key], options), Rank(right[key], options), "Int");
                if (compared != 0)
                {
                    return descending ? -compared : compared;
                }
            }

            var newest = string.CompareOrdinal(Text(right["createdOn"]), Text(left["createdOn"]));
            return newest != 0 ? newest : string.CompareOrdinal(Text(left["id"]), Text(right["id"]));
        };
    }

    // Nulls sort last in either direction's ascending form; numbers compare numerically, text case-insensitively.
    private static int CompareValues(JsonNode? left, JsonNode? right, string? typeName)
    {
        if (left is null || right is null)
        {
            return (left is null) == (right is null) ? 0 : left is null ? 1 : -1;
        }

        if (typeName is "Int" or "Decimal" or "Money" &&
            decimal.TryParse(left.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
            decimal.TryParse(right.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
        {
            return a.CompareTo(b);
        }

        if (left is JsonValue lb && right is JsonValue rb && lb.TryGetValue<bool>(out var x) && rb.TryGetValue<bool>(out var y))
        {
            return x.CompareTo(y);
        }

        return string.Compare(Text(left), Text(right), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? Rank(JsonNode? value, List<string> options) =>
        value is null ? null : JsonValue.Create(options.IndexOf(Text(value)));

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? string.Empty;
}
