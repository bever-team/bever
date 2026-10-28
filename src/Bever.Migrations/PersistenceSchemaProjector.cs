using System.Text.Json;
using Bever.Model;

namespace Bever.Migrations;

public sealed class PersistenceSchemaProjector
{
    private static readonly HashSet<string> SupportedPrimitives = new(StringComparer.Ordinal)
    {
        "Text", "Boolean", "Int", "Decimal", "Money", "DateTime", "Date", "Time", "Email", "Phone", "Url"
    };

    private static readonly PersistenceField[] SystemFields =
    [
        Primitive("Id", "Text", nullable: false),
        Primitive("CreatedOn", "DateTime", nullable: false),
        Primitive("CreatedBy", "Text", nullable: true),
        Primitive("ModifiedOn", "DateTime", nullable: false),
        Primitive("ModifiedBy", "Text", nullable: true),
        Primitive("DeletedOn", "DateTime", nullable: true),
        Primitive("DeletedBy", "Text", nullable: true)
    ];

    public PersistenceSchema Project(BeverModel model)
    {
        var entityNames = model.Entities.Select(entity => entity.Name).ToHashSet(StringComparer.Ordinal);
        var schema = new PersistenceSchema();

        foreach (var entity in model.Entities.OrderBy(entity => entity.Name, StringComparer.Ordinal))
        {
            var fieldNames = entity.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
            var reserved = SystemFields.FirstOrDefault(field => fieldNames.Contains(field.Name));
            if (reserved is not null)
            {
                throw new MigrationException($"Entity '{entity.Name}' declares reserved system field '{reserved.Name}'.");
            }

            var projected = new PersistenceEntity { Name = entity.Name };
            projected.Fields.AddRange(SystemFields.Select(Clone));
            projected.Fields.AddRange(entity.Fields
                .OrderBy(field => field.Name, StringComparer.Ordinal)
                .Select(field => ProjectField(entity.Name, field, entityNames)));
            schema.Entities.Add(projected);
        }

        return schema;
    }

    private static PersistenceField ProjectField(string entityName, FieldModel field, HashSet<string> entityNames)
    {
        var type = field.Type ?? throw new MigrationException($"Field '{entityName}.{field.Name}' has no valid type.");
        return type.Kind switch
        {
            "Primitive" => ProjectPrimitive(entityName, field.Name, type),
            "Enum" => new PersistenceField
            {
                Name = field.Name,
                Kind = "Enum",
                Nullable = type.Nullable,
                EnumValues = type.Options.Select(option => option.Name).Order(StringComparer.Ordinal).ToList()
            },
            "Relationship" => ProjectRelationship(entityName, field.Name, type, entityNames),
            _ => throw new MigrationException($"Field '{entityName}.{field.Name}' uses unsupported persistence type '{type.Kind}'.")
        };
    }

    private static PersistenceField ProjectPrimitive(string entityName, string fieldName, FieldTypeModel type)
    {
        if (type.Name is null || !SupportedPrimitives.Contains(type.Name))
        {
            throw new MigrationException($"Field '{entityName}.{fieldName}' uses unsupported primitive type '{type.Name}'.");
        }

        int? maxLength = null;
        if (type.Name == "Text" && type.Arguments.Count > 0 && type.Arguments[0].TryGetValue("maxLength", out var value))
        {
            maxLength = value switch
            {
                int integer => integer,
                long integer => checked((int)integer),
                JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var integer) => integer,
                _ => null
            };
        }

        return new PersistenceField
        {
            Name = fieldName,
            Kind = "Primitive",
            TypeName = type.Name,
            Nullable = type.Nullable,
            MaxLength = maxLength
        };
    }

    private static PersistenceField ProjectRelationship(string entityName, string fieldName, FieldTypeModel type, HashSet<string> entityNames)
    {
        if (type.Targets.Count != 1)
        {
            throw new MigrationException($"Field '{entityName}.{fieldName}' is polymorphic; the first migration format supports one relationship target.");
        }

        var target = type.Targets[0];
        var targetKind = string.Equals(type.TargetKind, "SystemIdentity", StringComparison.Ordinal) || target == "Identity"
            ? "SystemIdentity"
            : "Application";
        if (targetKind == "Application" && !entityNames.Contains(target))
        {
            throw new MigrationException($"Field '{entityName}.{fieldName}' references unknown entity '{target}'.");
        }

        return new PersistenceField
        {
            Name = fieldName,
            Kind = "Relationship",
            Nullable = type.Nullable,
            RelationshipTarget = target,
            RelationshipTargetKind = targetKind,
            OnDelete = type.OnDelete ?? "Restrict"
        };
    }

    private static PersistenceField Primitive(string name, string typeName, bool nullable) => new()
    {
        Name = name,
        Kind = "Primitive",
        TypeName = typeName,
        Nullable = nullable,
        System = true
    };

    private static PersistenceField Clone(PersistenceField field) => new()
    {
        Name = field.Name,
        Kind = field.Kind,
        TypeName = field.TypeName,
        Nullable = field.Nullable,
        System = field.System,
        MaxLength = field.MaxLength,
        EnumValues = field.EnumValues.ToList(),
        RelationshipTarget = field.RelationshipTarget,
        RelationshipTargetKind = field.RelationshipTargetKind,
        OnDelete = field.OnDelete
    };
}

public sealed class MigrationException : Exception
{
    public MigrationException(string message)
        : base(message)
    {
    }
}
