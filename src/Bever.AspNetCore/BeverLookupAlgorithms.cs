using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bever.AspNetCore;

internal static class BeverLookupAlgorithms
{
    public static BeverLookupResult Lookup(BeverLookupRequest request, IEnumerable<JsonObject> records)
    {
        var candidates = records
            .Where(record => !IsDeleted(record) && (request.Scope?.Allows(record) ?? true))
            .Select(record => ToCandidate(request.Entity, record, request.LabelField))
            .Select(candidate => candidate with
            {
                Rank = Rank(request.Entity, candidate.Record, request.SearchFields, request.NormalizedSearch),
                NormalizedLabel = NormalizeSearchText(candidate.Label)
            })
            .Where(candidate => Matches(request.Entity, candidate.Record, request.SearchFields, request.NormalizedSearch))
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.NormalizedLabel, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToList();

        var cursor = DecodeCursor(request.Cursor, request);
        if (cursor is not null)
        {
            candidates = candidates.Where(candidate => Compare(candidate, cursor) > 0).ToList();
        }

        var page = candidates.Take(request.Limit).ToList();
        var nextCursor = candidates.Count > request.Limit && page.Count > 0 ? EncodeCursor(request, page[^1]) : null;
        return new BeverLookupResult(page.Select(candidate => new BeverLookupItem(candidate.Id, candidate.Label)).ToList(), nextCursor);
    }

    public static IReadOnlyList<BeverLookupItem> Hydrate(BeverLookupHydrationRequest request, IEnumerable<JsonObject> records)
    {
        var byId = records
            .Where(record => !IsDeleted(record) && (request.Scope?.Allows(record) ?? true))
            .Select(record => ToCandidate(request.Entity, record, request.LabelField))
            .ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);

        var items = new List<BeverLookupItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in request.Ids)
        {
            if (!seen.Add(id))
            {
                continue;
            }

            if (byId.TryGetValue(id, out var candidate))
            {
                items.Add(new BeverLookupItem(candidate.Id, candidate.Label));
            }
        }

        return items;
    }

    public static string NormalizeSearchText(string value)
    {
        var builder = new StringBuilder();
        var pendingSpace = false;
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune.Value is >= 'A' and <= 'Z' ? (char)(rune.Value + 32) : rune.ToString());
        }

        return builder.ToString();
    }

    public static string FormatFieldValue(BeverEntityContext entity, JsonObject record, string fieldName)
    {
        if (fieldName.Length == 0)
        {
            return string.Empty;
        }

        var node = record[JsonPropertyName(fieldName)];
        if (node is null)
        {
            return string.Empty;
        }

        var field = entity.Model.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        var type = field?.Type;
        try
        {
            if (type?.Kind == "Primitive")
            {
                return type.Name switch
                {
                    "Int" => node.GetValue<long>().ToString(CultureInfo.InvariantCulture),
                    "Decimal" => node.GetValue<decimal>().ToString(CultureInfo.InvariantCulture),
                    "Date" => node.GetValue<string>(),
                    "DateTime" => FormatDateTime(node.GetValue<string>()),
                    "Boolean" => node.GetValue<bool>() ? "true" : "false",
                    _ => node.GetValue<string>()
                };
            }

            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return node.ToJsonString();
        }
    }

    private static LookupCandidate ToCandidate(BeverEntityContext entity, JsonObject record, string labelField)
    {
        var id = record["id"]?.GetValue<string>() ?? string.Empty;
        return new LookupCandidate(id, FormatFieldValue(entity, record, labelField), record);
    }

    private static bool Matches(BeverEntityContext entity, JsonObject record, IReadOnlyList<string> searchFields, string normalizedSearch)
    {
        if (normalizedSearch.Length == 0)
        {
            return true;
        }

        return searchFields.Any(field => NormalizeSearchText(FormatFieldValue(entity, record, field)).Contains(normalizedSearch, StringComparison.Ordinal));
    }

    private static int Rank(BeverEntityContext entity, JsonObject record, IReadOnlyList<string> searchFields, string normalizedSearch)
    {
        if (normalizedSearch.Length == 0)
        {
            return 0;
        }

        return searchFields.Any(field => NormalizeSearchText(FormatFieldValue(entity, record, field)).StartsWith(normalizedSearch, StringComparison.Ordinal)) ? 0 : 1;
    }

    private static string EncodeCursor(BeverLookupRequest request, LookupCandidate last)
    {
        var json = JsonSerializer.Serialize(new CursorPayload("1", request.Entity.RouteName, request.Picker, request.NormalizedSearch, request.SearchFields, request.Limit, last.Rank, last.NormalizedLabel, last.Id));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static CursorPayload? DecodeCursor(string? cursor, BeverLookupRequest request)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        if (cursor.Any(character => character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_'))
        {
            throw new BeverLookupInvalidCursorException();
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var payload = JsonSerializer.Deserialize<CursorPayload>(Encoding.UTF8.GetString(Convert.FromBase64String(padded)))
                ?? throw new InvalidOperationException();
            if (payload.Version != "1"
                || payload.EntityRoute != request.Entity.RouteName
                || payload.Picker != request.Picker
                || payload.NormalizedSearch != request.NormalizedSearch
                || payload.Limit != request.Limit
                || payload.Rank < 0
                || payload.NormalizedLabel is null
                || payload.Id is null
                || payload.SearchFields is null
                || !payload.SearchFields.SequenceEqual(request.SearchFields, StringComparer.Ordinal))
            {
                throw new InvalidOperationException();
            }

            return payload;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException or ArgumentException)
        {
            throw new BeverLookupInvalidCursorException();
        }
    }

    private static int Compare(LookupCandidate candidate, CursorPayload cursor)
    {
        var rank = candidate.Rank.CompareTo(cursor.Rank);
        if (rank != 0)
        {
            return rank;
        }

        var label = string.CompareOrdinal(candidate.NormalizedLabel, cursor.NormalizedLabel);
        return label != 0 ? label : string.CompareOrdinal(candidate.Id, cursor.Id);
    }

    private static string FormatDateTime(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant))
        {
            return instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : value;
    }

    private static bool IsDeleted(JsonObject value) => value["deletedOn"] is not null;

    private static string JsonPropertyName(string fieldName) => char.ToLowerInvariant(fieldName[0]) + fieldName[1..];

    private sealed record LookupCandidate(string Id, string Label, JsonObject Record)
    {
        public int Rank { get; init; }

        public string NormalizedLabel { get; init; } = string.Empty;
    }

    private sealed record CursorPayload(string Version, string EntityRoute, string Picker, string NormalizedSearch, IReadOnlyList<string> SearchFields, int Limit, int Rank, string NormalizedLabel, string Id);
}
