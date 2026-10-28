namespace Bever.Migrations;

public sealed class PersistenceSchema
{
    public string FormatVersion { get; set; } = "1";

    public List<PersistenceEntity> Entities { get; set; } = [];
}

public sealed class PersistenceEntity
{
    public string Name { get; set; } = string.Empty;

    public List<PersistenceField> Fields { get; set; } = [];
}

public sealed class PersistenceField
{
    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string? TypeName { get; set; }

    public bool Nullable { get; set; }

    public bool System { get; set; }

    public int? MaxLength { get; set; }

    public List<string> EnumValues { get; set; } = [];

    public string? RelationshipTarget { get; set; }

    public string? RelationshipTargetKind { get; set; }

    public string? OnDelete { get; set; }
}
