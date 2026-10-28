using System.Text.Json.Serialization;

namespace Bever.Model;

public sealed class BeverModel
{
    /// <summary>The Bever language version this model was compiled from.</summary>
    public const string LanguageVersion = "0.6";

    /// <summary>The semantic model contract version. Runtimes accept exactly this version.</summary>
    public const string ModelVersion = "0.10";

    public BeverModel()
    {
    }

    public BeverModel(
        string beverLanguageVersion,
        string semanticModelVersion,
        IReadOnlyList<EntityModel> entities,
        IReadOnlyList<ActionModel> actions)
        : this(beverLanguageVersion, semanticModelVersion, IdentityModel.Disabled(), entities, actions)
    {
    }

    public BeverModel(
        string beverLanguageVersion,
        string semanticModelVersion,
        IdentityModel identity,
        IReadOnlyList<EntityModel> entities,
        IReadOnlyList<ActionModel> actions,
        IReadOnlyList<RoleModel>? roles = null)
    {
        BeverLanguageVersion = beverLanguageVersion;
        SemanticModelVersion = semanticModelVersion;
        Identity = identity;
        Roles = roles?.ToList() ?? [];
        Entities = entities.ToList();
        Actions = actions.ToList();
    }

    public static BeverModel CreateEmpty() => new(LanguageVersion, ModelVersion, IdentityModel.Disabled(), [], []);

    [JsonPropertyOrder(1)]
    public string BeverLanguageVersion { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string SemanticModelVersion { get; set; } = string.Empty;

    [JsonPropertyOrder(3)]
    public IdentityModel Identity { get; set; } = IdentityModel.Disabled();

    [JsonPropertyOrder(4)]
    public List<RoleModel> Roles { get; set; } = [];

    [JsonPropertyOrder(5)]
    public List<EntityModel> Entities { get; set; } = [];

    [JsonPropertyOrder(6)]
    public List<ActionModel> Actions { get; set; } = [];
}

/// <summary>A role that accounts are assigned to outside the model; <c>By &lt;Role&gt;</c> allows its members.</summary>
public sealed class RoleModel
{
    public RoleModel()
    {
    }

    public RoleModel(string name, SourceSpan sourceSpan)
    {
        Name = name;
        SourceSpan = sourceSpan;
    }

    public string Name { get; set; } = string.Empty;

    public SourceSpan? SourceSpan { get; set; }
}

public sealed class IdentityModel
{
    public static IdentityModel Disabled() => new(false, [], null, []);

    public IdentityModel()
    {
    }

    public IdentityModel(bool enabled, IReadOnlyList<string> capabilities, IdentitySummaryModel? summary, IReadOnlyList<IdentitySystemTargetModel> systemTargets)
    {
        Enabled = enabled;
        Capabilities = capabilities.ToList();
        Summary = summary;
        SystemTargets = systemTargets.ToList();
    }

    [JsonPropertyOrder(1)]
    public bool Enabled { get; set; }

    [JsonPropertyOrder(2)]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyOrder(3)]
    public IdentitySummaryModel? Summary { get; set; }

    [JsonPropertyOrder(4)]
    public List<IdentitySystemTargetModel> SystemTargets { get; set; } = [];
}

public sealed class IdentitySummaryModel
{
    public IdentitySummaryModel()
    {
    }

    public IdentitySummaryModel(string labelSource) => LabelSource = labelSource;

    [JsonPropertyOrder(1)]
    public string LabelSource { get; set; } = string.Empty;
}

public sealed class IdentitySystemTargetModel
{
    public IdentitySystemTargetModel()
    {
    }

    public IdentitySystemTargetModel(string name, string kind, IdentityLookupModel lookup)
    {
        Name = name;
        Kind = kind;
        Lookup = lookup;
    }

    [JsonPropertyOrder(1)]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyOrder(3)]
    public IdentityLookupModel? Lookup { get; set; }
}

public sealed class IdentityLookupModel
{
    public IdentityLookupModel()
    {
    }

    public IdentityLookupModel(string summaryShape, bool contextBound, bool generalReadAction)
    {
        SummaryShape = summaryShape;
        ContextBound = contextBound;
        GeneralReadAction = generalReadAction;
    }

    [JsonPropertyOrder(1)]
    public string SummaryShape { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public bool ContextBound { get; set; }

    [JsonPropertyOrder(3)]
    public bool GeneralReadAction { get; set; }
}

public sealed class EntityModel
{
    public EntityModel()
    {
    }

    public EntityModel(string name, SourceSpan sourceSpan, IReadOnlyList<FieldModel> fields)
    {
        Name = name;
        Kind = "Application";
        SourceSpan = sourceSpan;
        Fields = fields.ToList();
    }

    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = "Application";

    public SourceSpan? SourceSpan { get; set; }

    public List<FieldModel> Fields { get; set; } = [];

    public List<RuleModel> Rules { get; set; } = [];

    public EntityPresentationModel? Presentation { get; set; }
}

/// <summary>A validation rule: a create or update is refused when <see cref="Condition"/> is false for the resulting record.</summary>
public sealed class RuleModel
{
    public RuleModel()
    {
    }

    public RuleModel(ExpressionModel condition, string message, SourceSpan sourceSpan)
    {
        Condition = condition;
        Message = message;
        SourceSpan = sourceSpan;
    }

    public ExpressionModel? Condition { get; set; }

    public string Message { get; set; } = string.Empty;

    public SourceSpan? SourceSpan { get; set; }
}

/// <summary>
/// A rule condition. Kinds: <c>And</c>, <c>Or</c>, <c>Not</c> (boolean operands); <c>Compare</c> (two operands and an
/// <see cref="Operator"/>); <c>In</c> (a field, then literals); and the values <c>Field</c> (<see cref="Path"/>),
/// <c>Literal</c> (<see cref="Literal"/>), <c>Null</c>, <c>Today</c>, and <c>Now</c>.
/// </summary>
public sealed class ExpressionModel
{
    public ExpressionModel()
    {
    }

    public ExpressionModel(string kind, string? @operator = null, IReadOnlyList<ExpressionModel>? operands = null, ReferencePathModel? path = null, LiteralValueModel? literal = null)
    {
        Kind = kind;
        Operator = @operator;
        Operands = operands?.ToList() ?? [];
        Path = path;
        Literal = literal;
    }

    public string Kind { get; set; } = string.Empty;

    public string? Operator { get; set; }

    public List<ExpressionModel> Operands { get; set; } = [];

    public ReferencePathModel? Path { get; set; }

    public LiteralValueModel? Literal { get; set; }
}

public sealed class EntityPresentationModel
{
    public EntityPresentationModel()
    {
    }

    public EntityPresentationModel(
        string? label,
        string? pluralLabel,
        ReferencePathModel? recordLabel,
        IReadOnlyList<PresentationFieldModel> fields,
        PresentationSurfaceModel? listing,
        PresentationSurfaceModel? details,
        InputExperiencePresentationModel? creating,
        InputExperiencePresentationModel? editing,
        IReadOnlyList<ActionPresentationOverrideModel> actionOverrides,
        SourceSpan sourceSpan)
    {
        Label = label;
        PluralLabel = pluralLabel;
        RecordLabel = recordLabel;
        Fields = fields.ToList();
        Listing = listing;
        Details = details;
        Creating = creating;
        Editing = editing;
        ActionOverrides = actionOverrides.ToList();
        SourceSpan = sourceSpan;
    }

    public string? Label { get; set; }

    public string? PluralLabel { get; set; }

    public ReferencePathModel? RecordLabel { get; set; }

    public List<PresentationFieldModel> Fields { get; set; } = [];

    public PresentationSurfaceModel? Listing { get; set; }

    public PresentationSurfaceModel? Details { get; set; }

    public InputExperiencePresentationModel? Creating { get; set; }

    public InputExperiencePresentationModel? Editing { get; set; }

    public List<ActionPresentationOverrideModel> ActionOverrides { get; set; } = [];

    public SourceSpan? SourceSpan { get; set; }
}

public sealed class PresentationSurfaceModel
{
    public PresentationSurfaceModel()
    {
    }

    public PresentationSurfaceModel(IReadOnlyList<ReferencePathModel>? fields, SourceSpan sourceSpan)
    {
        Fields = fields?.ToList();
        SourceSpan = sourceSpan;
    }

    public List<ReferencePathModel>? Fields { get; set; }

    public SourceSpan? SourceSpan { get; set; }
}

public sealed class InputExperiencePresentationModel
{
    public InputExperiencePresentationModel()
    {
    }

    public InputExperiencePresentationModel(
        string mode,
        IReadOnlyList<ReferencePathModel>? fields,
        IReadOnlyList<InputPresentationModel> inputs,
        SourceSpan sourceSpan)
    {
        Mode = mode;
        Fields = fields?.ToList();
        Inputs = inputs.ToList();
        SourceSpan = sourceSpan;
    }

    public string Mode { get; set; } = string.Empty;

    public List<ReferencePathModel>? Fields { get; set; }

    public List<InputPresentationModel> Inputs { get; set; } = [];

    public SourceSpan? SourceSpan { get; set; }
}

public sealed class PresentationFieldModel
{
    public PresentationFieldModel()
    {
    }

    public PresentationFieldModel(ReferencePathModel field, string label, SourceSpan sourceSpan)
    {
        Field = field;
        Label = label;
        SourceSpan = sourceSpan;
    }

    public ReferencePathModel? Field { get; set; }

    public string Label { get; set; } = string.Empty;

    public SourceSpan? SourceSpan { get; set; }
}

public sealed class InputPresentationModel
{
    public InputPresentationModel()
    {
    }

    public InputPresentationModel(ReferencePathModel field, string widget, string display, SourceSpan sourceSpan)
    {
        Field = field;
        Widget = widget;
        Display = display;
        SourceSpan = sourceSpan;
    }

    public ReferencePathModel? Field { get; set; }

    public string Widget { get; set; } = string.Empty;

    public string Display { get; set; } = string.Empty;

    public SourceSpan? SourceSpan { get; set; }
}

public sealed class ActionPresentationOverrideModel
{
    public ActionPresentationOverrideModel()
    {
    }

    public ActionPresentationOverrideModel(
        ActionReferenceModel action,
        string? label,
        string? emphasis,
        string? confirm,
        IReadOnlyList<string>? placements,
        int? order,
        bool? hidden,
        SourceSpan sourceSpan)
    {
        Action = action;
        Label = label;
        Emphasis = emphasis;
        Confirm = confirm;
        Placements = placements?.ToList();
        Order = order;
        Hidden = hidden;
        SourceSpan = sourceSpan;
    }

    public ActionReferenceModel? Action { get; set; }

    public string? Label { get; set; }

    public string? Emphasis { get; set; }

    public string? Confirm { get; set; }

    public List<string>? Placements { get; set; }

    public int? Order { get; set; }

    public bool? Hidden { get; set; }

    public SourceSpan? SourceSpan { get; set; }
}

public sealed class ActionReferenceModel
{
    public ActionReferenceModel()
    {
    }

    public ActionReferenceModel(string name)
    {
        Name = name;
    }

    public string Name { get; set; } = string.Empty;
}

public sealed class FieldModel
{
    public FieldModel()
    {
    }

    public FieldModel(string name, FieldTypeModel? type, LiteralValueModel? defaultValue, SourceSpan sourceSpan)
    {
        Name = name;
        Type = type;
        DefaultValue = defaultValue;
        SourceSpan = sourceSpan;
    }

    public string Name { get; set; } = string.Empty;

    public FieldTypeModel? Type { get; set; }

    public LiteralValueModel? DefaultValue { get; set; }

    public SourceSpan? SourceSpan { get; set; }
}

public sealed class FieldTypeModel
{
    public FieldTypeModel()
    {
    }

    public FieldTypeModel(
        string kind,
        string? name,
        bool nullable,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> arguments,
        IReadOnlyList<EnumOptionModel> options,
        IReadOnlyList<string> targets,
        string? onDelete = null,
        string? targetKind = null,
        string? picker = null)
    {
        Kind = kind;
        Name = name;
        Nullable = nullable;
        Arguments = arguments.Select(argument => argument.ToDictionary()).ToList();
        Options = options.ToList();
        Targets = targets.ToList();
        OnDelete = onDelete;
        TargetKind = targetKind;
        Picker = picker;
    }

    public string Kind { get; set; } = string.Empty;

    public string? Name { get; set; }

    public bool Nullable { get; set; }

    public List<Dictionary<string, object?>> Arguments { get; set; } = [];

    public List<EnumOptionModel> Options { get; set; } = [];

    public List<string> Targets { get; set; } = [];

    public string? OnDelete { get; set; }

    public string? TargetKind { get; set; }

    public string? Picker { get; set; }
}

public sealed class EnumOptionModel
{
    public EnumOptionModel()
    {
    }

    public EnumOptionModel(string name, object? value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; set; } = string.Empty;

    public object? Value { get; set; }
}

public sealed class LiteralValueModel
{
    public LiteralValueModel()
    {
    }

    public LiteralValueModel(string kind, object? value)
    {
        Kind = kind;
        Value = value;
    }

    public string Kind { get; set; } = string.Empty;

    public object? Value { get; set; }
}

public sealed class ActionModel
{
    public ActionModel()
    {
    }

    public ActionModel(
        string verb,
        string entity,
        SourceSpan sourceSpan,
        IReadOnlyList<AuthorizationModel> authorizations,
        OrderByModel? orderBy,
        IReadOnlyList<ReferencePathModel>? searchBy = null,
        string? name = null)
    {
        Verb = verb;
        Entity = entity;
        Name = name;
        SourceSpan = sourceSpan;
        Authorizations = authorizations.ToList();
        OrderBy = orderBy;
        SearchBy = searchBy?.ToList();
    }

    [JsonPropertyOrder(1)]
    public string Verb { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string Entity { get; set; } = string.Empty;

    [JsonPropertyOrder(3)]
    public string? Name { get; set; }

    [JsonPropertyOrder(4)]
    public SourceSpan? SourceSpan { get; set; }

    [JsonPropertyOrder(5)]
    public List<AuthorizationModel> Authorizations { get; set; } = [];

    [JsonPropertyOrder(6)]
    public OrderByModel? OrderBy { get; set; }

    [JsonPropertyOrder(7)]
    public List<ReferencePathModel>? SearchBy { get; set; }
}

public sealed class AuthorizationModel
{
    public AuthorizationModel()
    {
    }

    public AuthorizationModel(string kind, ReferencePathModel? path, string? role = null)
    {
        Kind = kind;
        Path = path;
        Role = role;
    }

    public string Kind { get; set; } = string.Empty;

    public ReferencePathModel? Path { get; set; }

    public string? Role { get; set; }
}

public sealed class OrderByModel
{
    public OrderByModel()
    {
    }

    public OrderByModel(ReferencePathModel path, string direction)
    {
        Path = path;
        Direction = direction;
    }

    public ReferencePathModel? Path { get; set; }

    public string Direction { get; set; } = string.Empty;
}

public sealed class ReferencePathModel
{
    public ReferencePathModel()
    {
    }

    public ReferencePathModel(IReadOnlyList<string> segments)
    {
        Segments = segments.ToList();
    }

    public List<string> Segments { get; set; } = [];
}

public sealed record SourceSpan(SourceLocation Start, SourceLocation End);

public sealed record SourceLocation(string Path, int Line, int Column)
{
    public override string ToString() => $"{Path}:{Line}:{Column}";
}
