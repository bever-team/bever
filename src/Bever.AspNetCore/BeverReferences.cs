using System.Text.Json.Nodes;
using Bever.Model;

namespace Bever.AspNetCore;

// Pickers, reference checks on save, and record labels. Read and Pick decide who sees which rows of an
// entity; a relationship field may only store a row its picker offers to the caller.
internal sealed class BeverReferences(BeverRuntime runtime, BeverAuthorizationEvaluator authorization, IBeverDatabaseProvider database, IBeverIdentityStore identities)
{
    public const string LabelsProperty = "_labels";

    // Read and every Pick of an entity: together they decide who may see its labels.
    public IReadOnlyList<BeverActionContext> SeeingActions(BeverEntityContext entity) =>
        runtime.Actions.Where(action => action.Entity == entity && action.Action is BeverEntityAction.Read or BeverEntityAction.Pick).ToList();

    // The named picker, otherwise the entity's default Pick, otherwise its Read.
    public BeverActionContext? ResolvePicker(BeverEntityContext entity, string? name)
    {
        var actions = SeeingActions(entity);
        return name is not null
            ? actions.FirstOrDefault(action => action.Action == BeverEntityAction.Pick && string.Equals(action.Model.Name, name, StringComparison.OrdinalIgnoreCase))
            : actions.FirstOrDefault(action => action is { Action: BeverEntityAction.Pick, Model.Name: null })
                ?? actions.FirstOrDefault(action => action.Action == BeverEntityAction.Read);
    }

    public async Task<IReadOnlyList<BeverEntityValidationError>> CheckAsync(BeverEntityContext entity, JsonObject values, JsonObject? current, CancellationToken cancellationToken)
    {
        var errors = new List<BeverEntityValidationError>();
        foreach (var field in entity.Model.Fields.Where(IsSingleTargetRelationship))
        {
            var name = BeverEntityRequestNormalizer.GetRequestFieldName(field);
            var id = StringValue(values[name]);
            if (id is null || id == StringValue(current?[name]) || await IsPickableAsync(field.Type!, id, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            // One message for missing, deleted, and unpickable rows, so saves never reveal which rows exist.
            errors.Add(new BeverEntityValidationError(name, "The selected record is unavailable."));
        }

        return errors;
    }

    public async Task AddLabelsAsync(BeverEntityContext entity, IReadOnlyList<JsonObject> records, CancellationToken cancellationToken)
    {
        var labelsByField = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var field in entity.Model.Fields.Where(IsSingleTargetRelationship))
        {
            var name = BeverEntityRequestNormalizer.GetRequestFieldName(field);
            var ids = records.Select(record => StringValue(record[name])).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            labelsByField[name] = ids.Length == 0 ? [] : await LabelsAsync(field.Type!, ids, cancellationToken).ConfigureAwait(false);
        }

        foreach (var record in records)
        {
            var labels = new JsonObject();
            foreach (var (name, labelsById) in labelsByField)
            {
                if (StringValue(record[name]) is { } id)
                {
                    labels[name] = labelsById.TryGetValue(id, out var label) ? label : null;
                }
            }

            record[LabelsProperty] = labels;
        }
    }

    // Omitted RecordLabel: the first Text field, otherwise the first other non-Boolean label-eligible field. Never the ID.
    public static string LabelField(EntityModel entity) =>
        entity.Presentation?.RecordLabel is { Segments.Count: 1 } recordLabel
            ? recordLabel.Segments[0]
            : (entity.Fields.FirstOrDefault(field => field.Type is { Kind: "Primitive", Name: "Text" })
                ?? entity.Fields.FirstOrDefault(field => field.Type?.Kind == "Enum" ||
                    (field.Type?.Kind == "Primitive" && field.Type.Name is "Email" or "Phone" or "Url" or "Int" or "Decimal" or "Date" or "DateTime")))?.Name
                ?? string.Empty;

    private async Task<Dictionary<string, string>> LabelsAsync(FieldTypeModel type, string[] ids, CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (type.TargetKind == "SystemIdentity")
        {
            // Identity has no Read: reading the containing record is what lets the caller see the label.
            foreach (var item in await identities.HydrateAsync(ids, cancellationToken).ConfigureAwait(false))
            {
                labels[item.Id] = item.Label;
            }

            return labels;
        }

        var target = Target(type);
        var (decision, scope) = await authorization.ResolveScopeAsync(SeeingActions(target), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            return labels;
        }

        // Limitation: 500-ID chunks keep SQLite under its parameter limit; page lists before they get much larger.
        foreach (var chunk in ids.Chunk(500))
        {
            foreach (var item in await database.HydrateLookupAsync(new BeverLookupHydrationRequest(target, LabelField(target.Model), chunk, scope), cancellationToken).ConfigureAwait(false))
            {
                labels[item.Id] = item.Label;
            }
        }

        return labels;
    }

    private async Task<bool> IsPickableAsync(FieldTypeModel type, string id, CancellationToken cancellationToken)
    {
        if (type.TargetKind == "SystemIdentity")
        {
            return await identities.FindByIdAsync(id, cancellationToken).ConfigureAwait(false) is not null;
        }

        var target = Target(type);
        if (ResolvePicker(target, type.Picker) is not { } picker)
        {
            return false;
        }

        var (decision, scope) = await authorization.ResolveScopeAsync([picker], cancellationToken).ConfigureAwait(false);
        return decision.Allowed &&
            (await database.HydrateLookupAsync(new BeverLookupHydrationRequest(target, string.Empty, [id], scope), cancellationToken).ConfigureAwait(false)).Count > 0;
    }

    private BeverEntityContext Target(FieldTypeModel type) =>
        runtime.Entities.First(entity => entity.Model.Name == type.Targets[0]);

    private static bool IsSingleTargetRelationship(FieldModel field) =>
        field.Type is { Kind: "Relationship", Targets.Count: 1 };

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
