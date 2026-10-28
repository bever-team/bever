using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bever.Migrations;

public static class SchemaJson
{
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(writeIndented: false);
    private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(writeIndented: true);

    public static string Serialize(PersistenceSchema schema) => JsonSerializer.Serialize(Normalize(schema), IndentedOptions);

    public static PersistenceSchema DeserializeSchema(string json)
    {
        var schema = JsonSerializer.Deserialize<PersistenceSchema>(json, CompactOptions)
            ?? throw new MigrationException("Persistence schema is empty.");
        if (schema.FormatVersion != "1")
        {
            throw new MigrationException($"Unsupported persistence schema format version '{schema.FormatVersion}'.");
        }

        return Normalize(schema);
    }

    public static string Serialize(MigrationArtifact migration) => JsonSerializer.Serialize(Normalize(migration), IndentedOptions);

    public static MigrationArtifact DeserializeMigration(string json)
    {
        var migration = JsonSerializer.Deserialize<MigrationArtifact>(json, CompactOptions)
            ?? throw new MigrationException("Migration artifact is empty.");
        if (migration.FormatVersion != "1")
        {
            throw new MigrationException($"Unsupported migration format version '{migration.FormatVersion}'.");
        }
        if (string.IsNullOrWhiteSpace(migration.Id)
            || string.IsNullOrWhiteSpace(migration.Name)
            || string.IsNullOrWhiteSpace(migration.FromSchemaHash)
            || string.IsNullOrWhiteSpace(migration.ToSchemaHash))
        {
            throw new MigrationException("Migration artifact is missing its identity or schema hashes.");
        }

        var supportedKinds = new HashSet<string>(["CreateEntity", "DropEntity", "AddField", "DropField", "AlterField"], StringComparer.Ordinal);
        var unknown = migration.Operations.FirstOrDefault(operation => !supportedKinds.Contains(operation.Kind));
        if (unknown is not null)
        {
            throw new MigrationException($"Unsupported migration operation kind '{unknown.Kind}'.");
        }

        return Normalize(migration);
    }

    public static string Hash(PersistenceSchema schema)
    {
        var canonicalJson = JsonSerializer.Serialize(Normalize(schema), CompactOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
    }

    internal static string CanonicalField(PersistenceField field) => JsonSerializer.Serialize(Normalize(field), CompactOptions);

    private static PersistenceSchema Normalize(PersistenceSchema schema) => new()
    {
        FormatVersion = schema.FormatVersion,
        Entities = schema.Entities
            .OrderBy(entity => entity.Name, StringComparer.Ordinal)
            .Select(Normalize)
            .ToList()
    };

    private static PersistenceEntity Normalize(PersistenceEntity entity) => new()
    {
        Name = entity.Name,
        Fields = entity.Fields.OrderBy(field => field.Name, StringComparer.Ordinal).Select(Normalize).ToList()
    };

    private static PersistenceField Normalize(PersistenceField field) => new()
    {
        Name = field.Name,
        Kind = field.Kind,
        TypeName = field.TypeName,
        Nullable = field.Nullable,
        System = field.System,
        MaxLength = field.MaxLength,
        EnumValues = field.EnumValues.Order(StringComparer.Ordinal).ToList(),
        RelationshipTarget = field.RelationshipTarget,
        RelationshipTargetKind = field.RelationshipTargetKind == "SystemIdentity" ? "SystemIdentity" : null,
        OnDelete = field.OnDelete
    };

    private static MigrationArtifact Normalize(MigrationArtifact migration) => new()
    {
        FormatVersion = migration.FormatVersion,
        Id = migration.Id,
        Name = migration.Name,
        FromSchemaHash = migration.FromSchemaHash,
        ToSchemaHash = migration.ToSchemaHash,
        Operations = migration.Operations
            .OrderBy(operation => operation.Entity, StringComparer.Ordinal)
            .ThenBy(operation => operation.Field?.Name ?? operation.FieldName, StringComparer.Ordinal)
            .ThenBy(operation => operation.Kind, StringComparer.Ordinal)
            .Select(Normalize)
            .ToList()
    };

    private static MigrationOperation Normalize(MigrationOperation operation) => new()
    {
        Kind = operation.Kind,
        Risk = operation.Risk,
        Entity = operation.Entity,
        EntityDefinition = operation.EntityDefinition is null ? null : Normalize(operation.EntityDefinition),
        FieldName = operation.FieldName,
        Field = operation.Field is null ? null : Normalize(operation.Field),
        PreviousField = operation.PreviousField is null ? null : Normalize(operation.PreviousField),
        Backfill = operation.Backfill is null
            ? null
            : new MigrationLiteral { Kind = operation.Backfill.Kind, Value = operation.Backfill.Value }
    };

    private static JsonSerializerOptions CreateOptions(bool writeIndented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = writeIndented
    };
}
