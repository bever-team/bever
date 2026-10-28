using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bever.AspNetCore;

public interface IBeverDatabaseProvider
{
    Task<IReadOnlyList<JsonObject>> ListAsync(BeverEntityContext entity, CancellationToken cancellationToken);

    Task<JsonObject?> GetAsync(BeverEntityContext entity, string id, CancellationToken cancellationToken);

    Task<JsonObject> CreateAsync(BeverEntityContext entity, JsonObject values, CancellationToken cancellationToken);

    Task<JsonObject?> PatchAsync(BeverEntityContext entity, string id, JsonObject values, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(BeverEntityContext entity, string id, string? actorId, CancellationToken cancellationToken);

    Task<BeverLookupResult> LookupAsync(BeverLookupRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<BeverLookupItem>> HydrateLookupAsync(BeverLookupHydrationRequest request, CancellationToken cancellationToken);
}

public sealed record BeverLookupRequest(
    BeverEntityContext Entity,
    string LabelField,
    IReadOnlyList<string> SearchFields,
    string NormalizedSearch,
    int Limit,
    string? Cursor,
    BeverRowScope? Scope = null,
    string Picker = "");

public sealed record BeverLookupHydrationRequest(
    BeverEntityContext Entity,
    string LabelField,
    IReadOnlyList<string> Ids,
    BeverRowScope? Scope = null);

/// <summary>
/// Rows a caller may see when not all rows are visible: rows whose owner field holds the actor's
/// identity ID, plus rows granted by ID. A null scope means every non-deleted row.
/// </summary>
public sealed record BeverRowScope(string ActorId, IReadOnlyList<string> OwnerFields, IReadOnlyList<string> RecordIds)
{
    public bool Allows(JsonObject record) =>
        OwnerFields.Any(field => record[JsonNamingPolicy.CamelCase.ConvertName(field)] is JsonValue value && value.TryGetValue<string>(out var owner) && owner == ActorId) ||
        (record["id"] is JsonValue id && id.TryGetValue<string>(out var recordId) && RecordIds.Contains(recordId, StringComparer.Ordinal));
}

public sealed record BeverLookupResult(IReadOnlyList<BeverLookupItem> Items, string? NextCursor);

public sealed record BeverLookupItem(string Id, string Label);
