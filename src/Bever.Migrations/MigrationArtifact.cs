namespace Bever.Migrations;

public sealed class MigrationArtifact
{
    public string FormatVersion { get; set; } = "1";

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string FromSchemaHash { get; set; } = string.Empty;

    public string ToSchemaHash { get; set; } = string.Empty;

    public List<MigrationOperation> Operations { get; set; } = [];
}

public sealed class MigrationOperation
{
    public string Kind { get; set; } = string.Empty;

    public string Risk { get; set; } = string.Empty;

    public string Entity { get; set; } = string.Empty;

    public PersistenceEntity? EntityDefinition { get; set; }

    public string? FieldName { get; set; }

    public PersistenceField? Field { get; set; }

    public PersistenceField? PreviousField { get; set; }

    public MigrationLiteral? Backfill { get; set; }
}

public sealed class MigrationLiteral
{
    public string Kind { get; set; } = string.Empty;

    public object? Value { get; set; }
}
