using System.Globalization;
using Bever.Model;

namespace Bever.Core;

internal sealed class PresentationSemanticBuilder
{
    private static readonly HashSet<string> RecordLabelPrimitiveTypes =
        new(["Text", "Email", "Phone", "Url", "Int", "Decimal", "Date", "DateTime", "Boolean"], StringComparer.Ordinal);

    private static readonly HashSet<string> Emphases =
        new(["Default", "Primary", "Destructive"], StringComparer.Ordinal);

    private static readonly HashSet<string> Placements =
        new(["Navigation", "ListingToolbar", "ListingRow", "DetailsToolbar"], StringComparer.Ordinal);

    private readonly DiagnosticBag diagnostics;
    private readonly IReadOnlyDictionary<string, EntityModel> entities;
    private readonly IReadOnlyList<ActionModel> actions;

    public PresentationSemanticBuilder(
        DiagnosticBag diagnostics,
        IReadOnlyDictionary<string, EntityModel> entities,
        IReadOnlyList<ActionModel> actions)
    {
        this.diagnostics = diagnostics;
        this.entities = entities;
        this.actions = actions;
    }

    public EntityPresentationModel? Build(EntityDeclarationSyntax entitySyntax, EntityModel entity)
    {
        var declarations = entitySyntax.Members.OfType<PresentDeclarationSyntax>().ToList();
        if (declarations.Count == 0)
        {
            return null;
        }

        foreach (var duplicate in declarations.Skip(1))
        {
            Error("Duplicate Present declaration.", duplicate.Span);
        }

        return BuildPresentation(declarations[0], entity);
    }

    private EntityPresentationModel BuildPresentation(PresentDeclarationSyntax syntax, EntityModel entity)
    {
        string? label = null;
        string? pluralLabel = null;
        ReferencePathModel? recordLabel = null;
        var fields = new List<PresentationFieldModel>();
        PresentationSurfaceModel? listing = null;
        PresentationSurfaceModel? details = null;
        InputExperiencePresentationModel? creating = null;
        InputExperiencePresentationModel? editing = null;
        var actionOverrides = new List<ActionPresentationOverrideModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fieldOverrides = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in syntax.Members)
        {
            var (name, value) = Split(member.Text);
            switch (name)
            {
                case "Label":
                    CheckUnique(seen, "Label", member.Span);
                    label = ParseQuoted(value, "Label", member);
                    RequireNoChildren(member);
                    break;
                case "PluralLabel":
                    CheckUnique(seen, "PluralLabel", member.Span);
                    pluralLabel = ParseQuoted(value, "PluralLabel", member);
                    RequireNoChildren(member);
                    break;
                case "RecordLabel":
                    CheckUnique(seen, "RecordLabel", member.Span);
                    recordLabel = ParseFieldPath(value, entity, member.Span, "RecordLabel");
                    RequireNoChildren(member);
                    if (recordLabel is not null)
                    {
                        ValidateRecordLabel(entity, recordLabel, member.Span);
                    }
                    break;
                case "Field":
                    var fieldPath = ParseFieldPath(value, entity, member.Span, "Field");
                    if (fieldPath is not null && !fieldOverrides.Add(FieldName(fieldPath)))
                    {
                        Error($"Duplicate presentation Field '{value}'.", member.Span);
                    }
                    var fieldLabel = ParseRequiredOnlyQuotedChild(member, "Label");
                    if (fieldPath is not null && fieldLabel is not null)
                    {
                        fields.Add(new PresentationFieldModel(fieldPath, fieldLabel, member.Span));
                    }
                    break;
                case "Listing":
                    CheckUnique(seen, "Listing", member.Span);
                    RequireEmptyValue(value, "Listing", member.Span);
                    listing = BuildSurface(member, entity, "Listing");
                    break;
                case "Details":
                    CheckUnique(seen, "Details", member.Span);
                    RequireEmptyValue(value, "Details", member.Span);
                    details = BuildSurface(member, entity, "Details");
                    break;
                case "Creating":
                    CheckUnique(seen, "Creating", member.Span);
                    RequireEmptyValue(value, "Creating", member.Span);
                    creating = BuildExperience(member, entity, creating: true);
                    break;
                case "Editing":
                    CheckUnique(seen, "Editing", member.Span);
                    RequireEmptyValue(value, "Editing", member.Span);
                    editing = BuildExperience(member, entity, creating: false);
                    break;
                case "Actions":
                    CheckUnique(seen, "Actions", member.Span);
                    RequireEmptyValue(value, "Actions", member.Span);
                    actionOverrides.AddRange(BuildActionOverrides(member, entity));
                    break;
                default:
                    RejectUnsupportedTree(member, "presentation");
                    break;
            }
        }

        ValidateExperienceAction(creating, "Create", entity, syntax.Span);
        ValidateExperienceAction(editing, "Update", entity, syntax.Span);

        return new EntityPresentationModel(
            label,
            pluralLabel,
            recordLabel,
            fields,
            listing,
            details,
            creating,
            editing,
            actionOverrides,
            syntax.Span);
    }

    private PresentationSurfaceModel BuildSurface(PresentationMemberSyntax syntax, EntityModel entity, string surface)
    {
        IReadOnlyList<ReferencePathModel>? fields = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in syntax.Members)
        {
            var (name, value) = Split(member.Text);
            if (name != "Fields")
            {
                RejectUnsupportedTree(member, surface);
                continue;
            }

            CheckUnique(seen, "Fields", member.Span);
            fields = ParseFieldProjection(value, entity, member.Span);
            RequireNoChildren(member);
        }

        return new PresentationSurfaceModel(fields, syntax.Span);
    }

    private InputExperiencePresentationModel BuildExperience(
        PresentationMemberSyntax syntax,
        EntityModel entity,
        bool creating)
    {
        string? mode = null;
        IReadOnlyList<ReferencePathModel>? fields = null;
        var inputs = new List<(InputPresentationModel Model, SourceSpan Span)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inputFields = new HashSet<string>(StringComparer.Ordinal);
        var experienceName = creating ? "Creating" : "Editing";

        foreach (var member in syntax.Members)
        {
            var (name, value) = Split(member.Text);
            switch (name)
            {
                case "Mode":
                    CheckUnique(seen, "Mode", member.Span);
                    mode = ParseMode(value, creating, member.Span);
                    RequireNoChildren(member);
                    break;
                case "Fields":
                    CheckUnique(seen, "Fields", member.Span);
                    fields = ParseFieldProjection(value, entity, member.Span);
                    RequireNoChildren(member);
                    break;
                case "Input":
                    var path = ParseFieldPath(value, entity, member.Span, "Input");
                    if (path is not null && !inputFields.Add(FieldName(path)))
                    {
                        Error($"Duplicate Input '{value}'.", member.Span);
                    }

                    var input = BuildInput(member, entity, path);
                    if (input is not null)
                    {
                        inputs.Add((input, member.Span));
                    }
                    break;
                default:
                    RejectUnsupportedTree(member, experienceName);
                    break;
            }
        }

        // An omitted Mode keeps the default experience: a page when the action exists.
        mode ??= actions.Any(action => action.Entity == entity.Name && action.Verb == (creating ? "Create" : "Update")) ? "Page" : "None";

        var allowedFields = fields is null
            ? entity.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal)
            : fields.Select(FieldName).ToHashSet(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var name = FieldName(input.Model.Field!);
            if (!allowedFields.Contains(name))
            {
                Error($"Input '{name}' is not one of the {experienceName} fields.", input.Span);
            }
        }

        return new InputExperiencePresentationModel(mode, fields, inputs.Select(item => item.Model).ToList(), syntax.Span);
    }

    private InputPresentationModel? BuildInput(
        PresentationMemberSyntax syntax,
        EntityModel entity,
        ReferencePathModel? path)
    {
        string? pickerDisplay = null;
        foreach (var member in syntax.Members)
        {
            var (name, value) = Split(member.Text);
            if (name != "Picker")
            {
                RejectUnsupportedTree(member, "Input");
                continue;
            }

            if (pickerDisplay is not null)
            {
                Error("Duplicate Picker declaration.", member.Span);
            }
            if (value is not "Dropdown" and not "Combobox" and not "Modal")
            {
                Error("Picker must be Dropdown, Combobox, or Modal.", member.Span);
            }
            else
            {
                pickerDisplay = value;
            }
            RequireNoChildren(member);
        }

        if (pickerDisplay is null)
        {
            Error("Input requires exactly one Picker declaration.", syntax.Span);
        }

        if (path is null || pickerDisplay is null)
        {
            return null;
        }

        var field = entity.Fields.First(item => item.Name == FieldName(path));
        var fieldType = field.Type;
        if (fieldType is null || fieldType.Kind != "Relationship" || fieldType.Targets.Count != 1)
        {
            Error($"Picker {pickerDisplay} requires a single-target relationship field.", syntax.Span);
        }
        else if (fieldType.TargetKind == "SystemIdentity")
        {
            // Identity picker access is context-bound to the containing Create/Update action; no Read Identity exists.
        }
        else if (fieldType.Picker is null && (!entities.ContainsKey(fieldType.Targets[0]) || !(HasAction(fieldType.Targets[0], "Read") || HasDefaultPick(fieldType.Targets[0]))))
        {
            Error($"Picker {pickerDisplay} target '{fieldType.Targets[0]}' must declare Pick or Read.", syntax.Span);
        }

        return new InputPresentationModel(path, "ReferencePicker", pickerDisplay, syntax.Span);
    }

    private IReadOnlyList<ActionPresentationOverrideModel> BuildActionOverrides(
        PresentationMemberSyntax syntax,
        EntityModel entity)
    {
        var result = new List<ActionPresentationOverrideModel>();
        var seenActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in syntax.Members)
        {
            var (name, value) = Split(member.Text);
            if (value.Length != 0)
            {
                Error("Action override must be a single action name.", member.Span);
            }

            if (!seenActions.Add(name))
            {
                Error($"Duplicate action override '{name}'.", member.Span);
            }

            if (string.Equals(name, "Pick", StringComparison.OrdinalIgnoreCase))
            {
                Error("Pick has no generated control, so presentation cannot override it.", member.Span);
                continue;
            }

            var action = actions.FirstOrDefault(item =>
                item.Entity == entity.Name && string.Equals(item.Verb, name, StringComparison.OrdinalIgnoreCase));
            if (action is null)
            {
                Error($"Presentation references undeclared action '{name}' for entity '{entity.Name}'.", member.Span);
            }

            result.Add(BuildActionOverride(member, action?.Verb ?? name));
        }

        return result;
    }

    private ActionPresentationOverrideModel BuildActionOverride(PresentationMemberSyntax syntax, string canonicalName)
    {
        string? label = null;
        string? emphasis = null;
        string? confirm = null;
        IReadOnlyList<string>? placements = null;
        int? order = null;
        bool? hidden = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in syntax.Members)
        {
            var (name, value) = Split(member.Text);
            CheckUnique(seen, name, member.Span);
            switch (name)
            {
                case "Label":
                    label = ParseQuoted(value, "Label", member);
                    break;
                case "Emphasis":
                    if (!Emphases.Contains(value))
                    {
                        Error($"Unknown Emphasis '{value}'.", member.Span);
                    }
                    else
                    {
                        emphasis = value;
                    }
                    break;
                case "Confirm":
                    confirm = ParseQuoted(value, "Confirm", member);
                    break;
                case "Place":
                    placements = ParsePlacements(value, canonicalName, member.Span);
                    break;
                case "Order":
                    if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedOrder))
                    {
                        Error("Order must be a whole number.", member.Span);
                    }
                    else
                    {
                        order = parsedOrder;
                    }
                    break;
                case "Hidden":
                    if (value == "true") hidden = true;
                    else if (value == "false") hidden = false;
                    else Error("Hidden must be lowercase true or false.", member.Span);
                    break;
                default:
                    RejectUnsupportedTree(member, "action presentation");
                    break;
            }
            RequireNoChildren(member);
        }

        return new ActionPresentationOverrideModel(
            new ActionReferenceModel(canonicalName),
            label,
            emphasis,
            confirm,
            placements,
            order,
            hidden,
            syntax.Span);
    }

    private IReadOnlyList<string>? ParsePlacements(string value, string action, SourceSpan span)
    {
        var values = value.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length == 0 || values.Any(item => item.Length == 0))
        {
            Error("Place requires at least one placement.", span);
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in values)
        {
            if (!Placements.Contains(placement))
            {
                Error($"Unknown placement '{placement}'.", span);
            }
            if (!seen.Add(placement))
            {
                Error($"Duplicate placement '{placement}'.", span);
            }
            if (!IsPlacementAllowed(action, placement))
            {
                Error($"Placement '{placement}' is not valid for action '{action}'.", span);
            }
        }
        return values;
    }

    private IReadOnlyList<ReferencePathModel> ParseFieldProjection(string value, EntityModel entity, SourceSpan span)
    {
        var values = value.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length == 0 || values.Any(item => item.Length == 0))
        {
            Error("Fields requires at least one field path.", span);
            return [];
        }

        var result = new List<ReferencePathModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var valuePart in values)
        {
            var path = ParseFieldPath(valuePart, entity, span, "Fields");
            if (path is null)
            {
                continue;
            }
            if (!seen.Add(FieldName(path)))
            {
                Error($"Duplicate field path '{valuePart}'.", span);
            }
            result.Add(path);
        }
        return result;
    }

    private ReferencePathModel? ParseFieldPath(string text, EntityModel entity, SourceSpan span, string owner)
    {
        var name = SemanticModelBuilder.ParseFieldName(text, diagnostics, span);
        if (name is null)
        {
            return null;
        }

        if (!IsIdentifier(name))
        {
            Error($"{owner} takes a field of this entity, for example Title.", span);
            return null;
        }

        if (!entity.Fields.Any(field => field.Name == name))
        {
            Error($"Unknown field path '{text}' on entity '{entity.Name}'.", span);
            return null;
        }
        return new ReferencePathModel([name]);
    }

    private void ValidateRecordLabel(EntityModel entity, ReferencePathModel path, SourceSpan span)
    {
        var field = entity.Fields.First(item => item.Name == FieldName(path));
        var valid = field.Type?.Kind == "Enum" ||
                    (field.Type?.Kind == "Primitive" && RecordLabelPrimitiveTypes.Contains(field.Type.Name ?? string.Empty));
        if (!valid)
        {
            Error($"RecordLabel field '{field.Name}' does not have an allowed display type.", span);
        }
    }

    private string? ParseRequiredOnlyQuotedChild(PresentationMemberSyntax syntax, string childName)
    {
        string? result = null;
        var seen = false;
        foreach (var child in syntax.Members)
        {
            var (name, value) = Split(child.Text);
            if (name != childName)
            {
                Error($"Unsupported Field presentation member '{name}'.", child.Span);
                continue;
            }
            if (seen)
            {
                Error($"Duplicate {childName} declaration.", child.Span);
            }
            seen = true;
            result = ParseQuoted(value, childName, child);
            RequireNoChildren(child);
        }
        if (!seen)
        {
            Error($"Field presentation requires exactly one {childName}.", syntax.Span);
        }
        return result;
    }

    private string ParseMode(string value, bool creating, SourceSpan span)
    {
        var valid = value is "Page" or "Modal" or "None" || (!creating && value == "Inline");
        if (!valid)
        {
            Error($"Unknown {(creating ? "Creating" : "Editing")} mode '{value}'.", span);
            return "None";
        }
        return value;
    }

    private void ValidateExperienceAction(
        InputExperiencePresentationModel? experience,
        string verb,
        EntityModel entity,
        SourceSpan span)
    {
        if (experience is not null && experience.Mode != "None" && !HasAction(entity.Name, verb))
        {
            Error($"{(verb == "Create" ? "Creating" : "Editing")} Mode {experience.Mode} requires action '{verb}'.", experience.SourceSpan ?? span);
        }
    }

    private bool HasAction(string entity, string verb) =>
        actions.Any(action => action.Entity == entity && action.Verb == verb);

    private bool HasDefaultPick(string entity) =>
        actions.Any(action => action is { Verb: "Pick", Name: null } && action.Entity == entity);

    private static bool IsPlacementAllowed(string action, string placement) => action switch
    {
        "Create" => placement == "ListingToolbar",
        "Read" => placement is "Navigation" or "ListingRow" or "DetailsToolbar",
        "Update" or "Delete" => placement is "ListingRow" or "DetailsToolbar",
        _ => false
    };

    private string? ParseQuoted(string value, string property, PresentationMemberSyntax syntax)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            Error($"{property} requires quoted text.", syntax.Span);
            return null;
        }
        return value[1..^1];
    }

    private void CheckUnique(HashSet<string> seen, string name, SourceSpan span)
    {
        if (!seen.Add(name))
        {
            Error($"Duplicate presentation declaration '{name}'.", span);
        }
    }

    private void RequireNoChildren(PresentationMemberSyntax syntax)
    {
        foreach (var child in syntax.Members)
        {
            Error($"'{syntax.Text}' does not accept nested presentation members.", child.Span);
        }
    }

    private void RejectUnsupportedTree(PresentationMemberSyntax syntax, string scope)
    {
        var (name, _) = Split(syntax.Text);
        Error($"Unsupported {scope} member '{name}'.", syntax.Span);
        foreach (var child in syntax.Members)
        {
            RejectUnsupportedTree(child, scope);
        }
    }

    private void RequireEmptyValue(string value, string declaration, SourceSpan span)
    {
        if (value.Length != 0)
        {
            Error($"{declaration} does not accept an inline value.", span);
        }
    }

    private void Error(string message, SourceSpan span) =>
        diagnostics.Report("BEV4001", DiagnosticSeverity.Error, message, span);

    private static (string Name, string Value) Split(string text)
    {
        var separator = text.IndexOf(' ');
        return separator < 0
            ? (text, string.Empty)
            : (text[..separator], text[(separator + 1)..].Trim());
    }

    private static bool IsIdentifier(string text)
    {
        if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_'))
        {
            return false;
        }
        return text.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static string FieldName(ReferencePathModel path) => path.Segments[0];
}
