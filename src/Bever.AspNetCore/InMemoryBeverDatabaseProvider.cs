using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Bever.AspNetCore;

public sealed class InMemoryBeverDatabaseProvider : IBeverDatabaseProvider
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, JsonObject>> recordsByEntity = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<JsonObject>> ListAsync(BeverEntityContext entity, CancellationToken cancellationToken)
    {
        var records = GetEntityRecords(entity).Values.Where(record => !IsDeleted(record)).Select(Clone).ToList();
        return Task.FromResult<IReadOnlyList<JsonObject>>(records);
    }

    public Task<JsonObject?> GetAsync(BeverEntityContext entity, string id, CancellationToken cancellationToken)
    {
        var records = GetEntityRecords(entity);
        return Task.FromResult(records.TryGetValue(id, out var record) && !IsDeleted(record) ? Clone(record) : null);
    }

    public Task<JsonObject> CreateAsync(BeverEntityContext entity, JsonObject values, CancellationToken cancellationToken)
    {
        var records = GetEntityRecords(entity);
        var id = Guid.NewGuid().ToString("N");
        var record = Clone(values);
        var now = DateTimeOffset.UtcNow.ToString("O");
        record["id"] = id;
        record["createdOn"] = now;
        record["modifiedOn"] = now;
        records[id] = record;
        return Task.FromResult(Clone(record));
    }

    public Task<JsonObject?> PatchAsync(BeverEntityContext entity, string id, JsonObject values, CancellationToken cancellationToken)
    {
        var records = GetEntityRecords(entity);
        while (true)
        {
            if (!records.TryGetValue(id, out var record) || IsDeleted(record))
            {
                return Task.FromResult<JsonObject?>(null);
            }

            var updated = Clone(record);
            foreach (var property in values)
            {
                updated[property.Key] = property.Value?.DeepClone();
            }

            updated["id"] = id;
            updated["modifiedOn"] = DateTimeOffset.UtcNow.ToString("O");
            if (records.TryUpdate(id, updated, record))
            {
                return Task.FromResult<JsonObject?>(Clone(updated));
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public Task<bool> DeleteAsync(BeverEntityContext entity, string id, string? actorId, CancellationToken cancellationToken)
    {
        var records = GetEntityRecords(entity);
        while (true)
        {
            if (!records.TryGetValue(id, out var record) || IsDeleted(record))
            {
                return Task.FromResult(false);
            }

            var deleted = Clone(record);
            deleted["deletedOn"] = DateTimeOffset.UtcNow;
            if (actorId is not null)
            {
                deleted["deletedBy"] = actorId;
                deleted["modifiedBy"] = actorId;
            }
            if (records.TryUpdate(id, deleted, record))
            {
                return Task.FromResult(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public Task<BeverLookupResult> LookupAsync(BeverLookupRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(BeverLookupAlgorithms.Lookup(request, GetEntityRecords(request.Entity).Values));
    }

    public Task<IReadOnlyList<BeverLookupItem>> HydrateLookupAsync(BeverLookupHydrationRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(BeverLookupAlgorithms.Hydrate(request, GetEntityRecords(request.Entity).Values));
    }

    private ConcurrentDictionary<string, JsonObject> GetEntityRecords(BeverEntityContext entity) =>
        recordsByEntity.GetOrAdd(entity.RouteName, _ => new ConcurrentDictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase));

    private static JsonObject Clone(JsonObject value) => (JsonObject)value.DeepClone();

    private static bool IsDeleted(JsonObject value) => value["deletedOn"] is not null;

    internal static string NormalizeSearchText(string value) => BeverLookupAlgorithms.NormalizeSearchText(value);
}

public sealed class BeverLookupInvalidCursorException : Exception
{
}
