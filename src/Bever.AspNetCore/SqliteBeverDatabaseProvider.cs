using System.Text.Json;
using System.Text.Json.Nodes;
using Bever.Sqlite;

namespace Bever.AspNetCore;

public sealed class SqliteBeverDatabaseProvider : IBeverDatabaseProvider
{
    private readonly BeverSqliteDatabase database;

    public SqliteBeverDatabaseProvider(BeverSqliteDatabase database)
    {
        this.database = database;
    }

    public Task<IReadOnlyList<JsonObject>> ListAsync(BeverEntityContext entity, CancellationToken cancellationToken) =>
        database.ListAsync(entity.Model.Name, cancellationToken);

    public Task<JsonObject?> GetAsync(BeverEntityContext entity, string id, CancellationToken cancellationToken) =>
        database.GetAsync(entity.Model.Name, id, cancellationToken);

    public Task<JsonObject> CreateAsync(BeverEntityContext entity, JsonObject values, CancellationToken cancellationToken) =>
        database.CreateAsync(entity.Model.Name, NormalizeEnums(entity, values), cancellationToken);

    public Task<JsonObject?> PatchAsync(BeverEntityContext entity, string id, JsonObject values, CancellationToken cancellationToken) =>
        database.PatchAsync(entity.Model.Name, id, NormalizeEnums(entity, values), cancellationToken);

    public Task<bool> DeleteAsync(BeverEntityContext entity, string id, string? actorId, CancellationToken cancellationToken) =>
        database.DeleteAsync(entity.Model.Name, id, actorId, cancellationToken);

    public async Task<BeverLookupResult> LookupAsync(BeverLookupRequest request, CancellationToken cancellationToken)
    {
        var columns = LookupColumns(request.Entity, request.LabelField, request.SearchFields, request.Scope);
        var records = await database.ListColumnsAsync(request.Entity.Model.Name, columns, null, cancellationToken).ConfigureAwait(false);
        return BeverLookupAlgorithms.Lookup(request, records);
    }

    public async Task<IReadOnlyList<BeverLookupItem>> HydrateLookupAsync(BeverLookupHydrationRequest request, CancellationToken cancellationToken)
    {
        var columns = LookupColumns(request.Entity, request.LabelField, [], request.Scope);
        var records = await database.ListColumnsAsync(request.Entity.Model.Name, columns, request.Ids, cancellationToken).ConfigureAwait(false);
        return BeverLookupAlgorithms.Hydrate(request, records);
    }

    private static IReadOnlyList<string> LookupColumns(BeverEntityContext entity, string labelField, IReadOnlyList<string> searchFields, BeverRowScope? scope) =>
        searchFields.Append(labelField).Concat(scope?.OwnerFields ?? [])
            .Where(field => entity.Model.Fields.Any(candidate => string.Equals(candidate.Name, field, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static JsonObject NormalizeEnums(BeverEntityContext entity, JsonObject values)
    {
        var normalized = (JsonObject)values.DeepClone();
        foreach (var field in entity.Model.Fields.Where(field => field.Type?.Kind == "Enum"))
        {
            var propertyName = JsonNamingPolicy.CamelCase.ConvertName(field.Name);
            if (!normalized.TryGetPropertyValue(propertyName, out var value) || value is null)
            {
                continue;
            }

            var option = field.Type!.Options.FirstOrDefault(option =>
                JsonNode.DeepEquals(value, JsonValue.Create(option.Name))
                || JsonNode.DeepEquals(value, JsonSerializer.SerializeToNode(option.Value)));
            if (option is not null)
            {
                normalized[propertyName] = option.Name;
            }
        }

        return normalized;
    }
}
