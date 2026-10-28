namespace Bever.Migrations;

public sealed class SchemaDiffer
{
    public IReadOnlyList<MigrationOperation> Diff(PersistenceSchema previous, PersistenceSchema current)
    {
        var operations = new List<MigrationOperation>();
        var previousEntities = previous.Entities.ToDictionary(entity => entity.Name, StringComparer.Ordinal);
        var currentEntities = current.Entities.ToDictionary(entity => entity.Name, StringComparer.Ordinal);

        foreach (var entity in current.Entities.Where(entity => !previousEntities.ContainsKey(entity.Name)))
        {
            operations.Add(new MigrationOperation
            {
                Kind = "CreateEntity",
                Risk = "Safe",
                Entity = entity.Name,
                EntityDefinition = entity
            });
        }

        foreach (var entity in previous.Entities.Where(entity => !currentEntities.ContainsKey(entity.Name)))
        {
            operations.Add(new MigrationOperation { Kind = "DropEntity", Risk = "Destructive", Entity = entity.Name });
        }

        foreach (var currentEntity in current.Entities.Where(entity => previousEntities.ContainsKey(entity.Name)))
        {
            DiffFields(previousEntities[currentEntity.Name], currentEntity, operations);
        }

        return operations
            .OrderBy(operation => operation.Entity, StringComparer.Ordinal)
            .ThenBy(operation => operation.Field?.Name ?? operation.FieldName, StringComparer.Ordinal)
            .ThenBy(operation => operation.Kind, StringComparer.Ordinal)
            .ToList();
    }

    private static void DiffFields(PersistenceEntity previous, PersistenceEntity current, List<MigrationOperation> operations)
    {
        var previousFields = previous.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var currentFields = current.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);

        foreach (var field in current.Fields.Where(field => !previousFields.ContainsKey(field.Name)))
        {
            operations.Add(new MigrationOperation
            {
                Kind = "AddField",
                Risk = field.Nullable ? "Safe" : "RequiresBackfill",
                Entity = current.Name,
                Field = field
            });
        }

        foreach (var field in previous.Fields.Where(field => !currentFields.ContainsKey(field.Name)))
        {
            operations.Add(new MigrationOperation
            {
                Kind = "DropField",
                Risk = "Destructive",
                Entity = current.Name,
                FieldName = field.Name
            });
        }

        foreach (var field in current.Fields.Where(field => previousFields.ContainsKey(field.Name)))
        {
            var previousField = previousFields[field.Name];
            if (SchemaJson.CanonicalField(previousField) == SchemaJson.CanonicalField(field))
            {
                continue;
            }

            operations.Add(new MigrationOperation
            {
                Kind = "AlterField",
                Risk = ClassifyAlter(previousField, field),
                Entity = current.Name,
                Field = field,
                PreviousField = previousField
            });
        }
    }

    private static string ClassifyAlter(PersistenceField previous, PersistenceField current)
    {
        if (previous.Kind != current.Kind || previous.TypeName != current.TypeName)
        {
            return "Destructive";
        }

        if (!previous.Nullable && current.Nullable)
        {
            return "Safe";
        }

        if (previous.Nullable && !current.Nullable)
        {
            return "RequiresBackfill";
        }

        // Changes that keep every existing value valid: a looser text limit, extra enum options, or another delete behavior.
        var wasNullable = previous.Nullable == current.Nullable;
        if (wasNullable && previous.Kind == "Primitive" && previous.TypeName == "Text" && previous.MaxLength is not null
            && (current.MaxLength is null || current.MaxLength >= previous.MaxLength))
        {
            return "Safe";
        }

        if (wasNullable && previous.Kind == "Enum" && previous.EnumValues.All(current.EnumValues.Contains))
        {
            return "Safe";
        }

        if (wasNullable && previous.Kind == "Relationship" && previous.RelationshipTarget == current.RelationshipTarget
            && previous.RelationshipTargetKind == current.RelationshipTargetKind)
        {
            return "Safe";
        }

        return "Destructive";
    }
}
