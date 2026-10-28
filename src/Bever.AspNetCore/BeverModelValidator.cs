using System.Diagnostics.CodeAnalysis;
using Bever.Model;

namespace Bever.AspNetCore;

internal static class BeverModelValidator
{
    private static readonly HashSet<string> RecordLabelPrimitiveTypes =
        new(["Text", "Email", "Phone", "Url", "Int", "Decimal", "Date", "DateTime", "Boolean"], StringComparer.Ordinal);

    private static readonly HashSet<string> SupportedPrimitiveTypes =
        new(["Text", "Boolean", "Int", "Decimal", "Money", "Date", "DateTime", "Time", "Email", "Phone", "Url"], StringComparer.Ordinal);

    private static readonly HashSet<string> SearchablePrimitiveTypes =
        new(["Text", "Email", "Phone", "Url"], StringComparer.Ordinal);

    private static readonly HashSet<string> PickerDisplays =
        new(["Dropdown", "Combobox", "Modal"], StringComparer.Ordinal);

    private static readonly HashSet<string> Emphases =
        new(["Default", "Primary", "Destructive"], StringComparer.Ordinal);

    private static readonly HashSet<string> Placements =
        new(["Navigation", "ListingToolbar", "ListingRow", "DetailsToolbar"], StringComparer.Ordinal);

    private static readonly HashSet<string> ActionVerbs =
        new(["Create", "Read", "Update", "Delete", "Pick"], StringComparer.Ordinal);

    public static void Validate(BeverModel model, string modelPath)
    {
        if (model.SemanticModelVersion != BeverModel.ModelVersion)
        {
            Fail(modelPath, "semanticModelVersion", $"unsupported Bever semantic model version '{model.SemanticModelVersion}'");
        }

        if (model.Identity is null || model.Roles is null || model.Entities is null || model.Actions is null)
        {
            Fail(modelPath, "$", "required model collections cannot be null");
        }

        ValidateIdentity(model, modelPath);
        ValidateRoles(model, modelPath);

        var entityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var entityIndex = 0; entityIndex < model.Entities.Count; entityIndex++)
        {
            var entity = model.Entities[entityIndex];
            if (entity is null || string.IsNullOrWhiteSpace(entity.Name))
            {
                Fail(modelPath, $"entities[{entityIndex}]", "entity name is required");
            }

            AddUnique(entityNames, entity.Name, $"entities[{entityIndex}].name", modelPath);
            if (string.Equals(entity.Name, "Identity", StringComparison.OrdinalIgnoreCase))
            {
                Fail(modelPath, $"entities[{entityIndex}].name", "entity name 'Identity' is reserved");
            }

            if (!string.Equals(entity.Kind, "Application", StringComparison.Ordinal))
            {
                Fail(modelPath, $"entities[{entityIndex}].kind", "entity kind must be 'Application'");
            }

            var routeName = EntityRouteName.FromEntityName(entity.Name);
            if (routeName is "_lookup" or "_identity-lookup" or "_identity")
            {
                Fail(modelPath, $"entities[{entityIndex}].name", $"entity route '{routeName}' is reserved");
            }
        }

        var actionIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var actionIndex = 0; actionIndex < model.Actions.Count; actionIndex++)
        {
            var action = model.Actions[actionIndex];
            if (action is null || string.IsNullOrWhiteSpace(action.Entity) || !ActionVerbs.Contains(action.Verb))
            {
                Fail(modelPath, $"actions[{actionIndex}]", "action must name a supported verb and entity");
            }
            if (!entityNames.Contains(action.Entity))
            {
                Fail(modelPath, $"actions[{actionIndex}].entity", $"unknown entity '{action.Entity}'");
            }

            if (action.Name is not null && (action.Verb != "Pick" || action.Name.Length == 0 || !char.IsLetter(action.Name[0]) || !action.Name.All(char.IsLetterOrDigit)))
            {
                Fail(modelPath, $"actions[{actionIndex}].name", "only Pick actions may carry a picker name, and it must be an identifier");
            }

            if (action.Verb != "Read" && action.OrderBy is not null)
            {
                Fail(modelPath, $"actions[{actionIndex}].orderBy", "only Read actions may declare orderBy");
            }

            AddUnique(actionIdentities, $"{action.Entity}:{action.Verb}:{action.Name}", $"actions[{actionIndex}]", modelPath);
        }

        var fieldsByEntity = new Dictionary<string, Dictionary<string, FieldModel>>(StringComparer.OrdinalIgnoreCase);

        for (var entityIndex = 0; entityIndex < model.Entities.Count; entityIndex++)
        {
            var entity = model.Entities[entityIndex];
            var entityPath = $"entities[{entityIndex}]";
            if (entity is null || string.IsNullOrWhiteSpace(entity.Name))
            {
                Fail(modelPath, entityPath, "entity name is required");
            }

            if (entity.Fields is null)
            {
                Fail(modelPath, $"{entityPath}.fields", "fields cannot be null");
            }

            var fields = new Dictionary<string, FieldModel>(StringComparer.OrdinalIgnoreCase);
            for (var fieldIndex = 0; fieldIndex < entity.Fields.Count; fieldIndex++)
            {
                var field = entity.Fields[fieldIndex];
                if (field is null || string.IsNullOrWhiteSpace(field.Name))
                {
                    Fail(modelPath, $"{entityPath}.fields[{fieldIndex}]", "field name is required");
                }

                if (!fields.TryAdd(field.Name, field))
                {
                    Fail(modelPath, $"{entityPath}.fields[{fieldIndex}].name", $"duplicate field name '{field.Name}'");
                }

                var supported = field.Type?.Kind switch
                {
                    "Primitive" => SupportedPrimitiveTypes.Contains(field.Type.Name ?? string.Empty),
                    "Enum" => field.Type.Options is { Count: > 0 },
                    "Relationship" => true,
                    _ => false
                };
                if (!supported)
                {
                    Fail(modelPath, $"{entityPath}.fields[{fieldIndex}].type", $"field '{field.Name}' has an unsupported type");
                }
            }

            fieldsByEntity[entity.Name] = fields;

            if (entity.Rules is null)
            {
                Fail(modelPath, $"{entityPath}.rules", "rules cannot be null");
            }

            for (var ruleIndex = 0; ruleIndex < entity.Rules.Count; ruleIndex++)
            {
                var rulePath = $"{entityPath}.rules[{ruleIndex}]";
                var rule = entity.Rules[ruleIndex];
                if (rule?.Condition is null || string.IsNullOrWhiteSpace(rule.Message))
                {
                    Fail(modelPath, rulePath, "a rule needs a condition and a message");
                }

                ValidateRuleCondition(rule.Condition, fields, $"{rulePath}.condition", modelPath);
            }

            ValidateRelationshipTargets(model, entity, entityPath, entityNames, modelPath);

            if (entity.Presentation is not null)
            {
                ValidatePresentation(model, entity, fields, entityPath, modelPath);
            }
        }

        ValidateActionAuthorizations(model, fieldsByEntity, modelPath);
        ValidateActionSearchBy(model, fieldsByEntity, modelPath);
        ValidatePickableForms(model, modelPath);
        ValidateTargetLabels(model, modelPath);
    }

    // The shape the runtime evaluates: boolean nodes over comparisons of a local field. Value types are the compiler's to check.
    // Limitation: a literal that does not parse as its field's type is not caught here; evaluation then fails the request.
    private static void ValidateRuleCondition(ExpressionModel expression, Dictionary<string, FieldModel> fields, string path, string modelPath)
    {
        var operands = expression.Operands ?? [];
        var valid = expression.Kind switch
        {
            "And" or "Or" => operands.Count >= 2,
            "Not" => operands.Count == 1,
            "Compare" => operands.Count == 2 && expression.Operator is "=" or "!=" or "<" or "<=" or ">" or ">=" && operands.Any(operand => operand.Kind == "Field"),
            "In" => operands.Count >= 2 && operands[0].Kind == "Field" && operands.Skip(1).All(operand => operand.Kind == "Literal"),
            _ => false
        };
        if (!valid)
        {
            Fail(modelPath, path, $"unsupported rule condition '{expression.Kind}'");
        }

        for (var index = 0; index < operands.Count; index++)
        {
            var operand = operands[index];
            var operandPath = $"{path}.operands[{index}]";
            if (expression.Kind is "And" or "Or" or "Not")
            {
                ValidateRuleCondition(operand, fields, operandPath, modelPath);
                continue;
            }

            var value = operand.Kind switch
            {
                "Field" => operand.Path?.Segments is [var name] && fields.ContainsKey(name),
                "Literal" => operand.Literal?.Value is not null,
                "Null" or "Today" or "Now" => true,
                _ => false
            };
            if (!value)
            {
                Fail(modelPath, operandPath, $"unsupported rule value '{operand.Kind}'");
            }
        }
    }

    // A relationship in an effective Create/Update projection must resolve to a picker, or no one could fill it in.
    private static void ValidatePickableForms(BeverModel model, string modelPath)
    {
        for (var entityIndex = 0; entityIndex < model.Entities.Count; entityIndex++)
        {
            var entity = model.Entities[entityIndex];
            var projected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (verb, experience) in new[] { ("Create", entity.Presentation?.Creating), ("Update", entity.Presentation?.Editing) })
            {
                if (experience?.Mode != "None" && HasAction(model, entity.Name, verb))
                {
                    projected.UnionWith(experience?.Fields?.Select(path => path.Segments[0]) ?? entity.Fields.Select(field => field.Name));
                }
            }

            for (var fieldIndex = 0; fieldIndex < entity.Fields.Count; fieldIndex++)
            {
                var field = entity.Fields[fieldIndex];
                if (projected.Contains(field.Name) && field.Type is { Kind: "Relationship", TargetKind: "Application", Picker: null } type && !IsPickable(model, type.Targets[0]))
                {
                    Fail(modelPath, $"entities[{entityIndex}].fields[{fieldIndex}]", $"relationship target '{type.Targets[0]}' declares no Pick or Read, so the field cannot be selected in a form");
                }
            }
        }
    }

    private static void ValidateTargetLabels(BeverModel model, string modelPath)
    {
        for (var entityIndex = 0; entityIndex < model.Entities.Count; entityIndex++)
        {
            var entity = model.Entities[entityIndex];
            var targeted = model.Entities.SelectMany(candidate => candidate.Fields)
                .Any(field => field.Type is { Kind: "Relationship", TargetKind: "Application" } type && type.Targets[0] == entity.Name);
            if (targeted && BeverReferences.LabelField(entity).Length == 0)
            {
                Fail(modelPath, $"entities[{entityIndex}]", "relationship target has no field that can label its records");
            }
        }
    }

    private static bool IsPickable(BeverModel model, string entity) =>
        model.Actions.Any(action => string.Equals(action.Entity, entity, StringComparison.OrdinalIgnoreCase) &&
            (action.Verb == "Read" || (action.Verb == "Pick" && action.Name is null)));

    private static void ValidateIdentity(BeverModel model, string modelPath)
    {
        var identity = model.Identity!;
        if (!identity.Enabled)
        {
            if (identity.Capabilities.Count != 0 || identity.Summary is not null || identity.SystemTargets.Count != 0)
            {
                Fail(modelPath, "identity", "disabled identity must not declare capabilities, summary, or system targets");
            }
            return;
        }

        var expectedCapabilities = identity.Capabilities.Contains("Register", StringComparer.Ordinal)
            ? new[] { "Login", "Logout", "Session", "Register" }
            : new[] { "Login", "Logout", "Session" };
        if (!identity.Capabilities.SequenceEqual(expectedCapabilities, StringComparer.Ordinal))
        {
            Fail(modelPath, "identity.capabilities", "identity capabilities must be ordered Login, Logout, Session, Register when present");
        }

        if (identity.Summary?.LabelSource != "Email")
        {
            Fail(modelPath, "identity.summary", "enabled identity requires Summary Label Email");
        }

        if (identity.SystemTargets.Count != 1 || identity.SystemTargets[0].Name != "Identity" || identity.SystemTargets[0].Kind != "SystemIdentity")
        {
            Fail(modelPath, "identity.systemTargets", "enabled identity requires exactly one SystemIdentity target named Identity");
        }

        var lookup = identity.SystemTargets[0].Lookup;
        if (lookup is null || lookup.SummaryShape != "IdLabel" || !lookup.ContextBound || lookup.GeneralReadAction)
        {
            Fail(modelPath, "identity.systemTargets[0].lookup", "identity lookup must be context-bound IdLabel without general read action");
        }
    }

    private static void ValidateRoles(BeverModel model, string modelPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < model.Roles.Count; index++)
        {
            var name = model.Roles[index]?.Name;
            if (string.IsNullOrEmpty(name) || !char.IsAsciiLetter(name[0]) || !name.All(char.IsAsciiLetterOrDigit) ||
                name is "Anyone" or "Authenticated" or "Administrator" or "Identity")
            {
                Fail(modelPath, $"roles[{index}].name", "role name must be an identifier other than Anyone, Authenticated, Administrator, or Identity");
            }

            AddUnique(names, name, $"roles[{index}].name", modelPath);
            if (!model.Identity.Enabled)
            {
                Fail(modelPath, $"roles[{index}]", "roles require enabled identity");
            }
        }
    }

    private static void ValidateRelationshipTargets(BeverModel model, EntityModel entity, string entityPath, HashSet<string> entityNames, string modelPath)
    {
        for (var fieldIndex = 0; fieldIndex < entity.Fields.Count; fieldIndex++)
        {
            var type = entity.Fields[fieldIndex].Type;
            if (type?.Kind != "Relationship")
            {
                continue;
            }

            if (type.Targets.Count != 1)
            {
                Fail(modelPath, $"{entityPath}.fields[{fieldIndex}].type.targets", "relationship must declare exactly one target");
            }

            var target = type.Targets[0];
            if (string.Equals(target, "Identity", StringComparison.OrdinalIgnoreCase))
            {
                if (!model.Identity.Enabled || type.TargetKind != "SystemIdentity" || target != "Identity" || type.Picker is not null)
                {
                    Fail(modelPath, $"{entityPath}.fields[{fieldIndex}].type", "Identity relationship target requires enabled identity, targetKind SystemIdentity, and no picker");
                }
            }
            else if (type.TargetKind != "Application" || !entityNames.Contains(target))
            {
                Fail(modelPath, $"{entityPath}.fields[{fieldIndex}].type", $"unknown application relationship target '{target}'");
            }
            else if (type.Picker is not null && !model.Actions.Any(action => action is { Verb: "Pick" } && action.Entity == target && action.Name == type.Picker))
            {
                Fail(modelPath, $"{entityPath}.fields[{fieldIndex}].type.picker", $"picker '{type.Picker}' is not declared on '{target}'");
            }
        }
    }

    private static void ValidateActionAuthorizations(BeverModel model, Dictionary<string, Dictionary<string, FieldModel>> fieldsByEntity, string modelPath)
    {
        for (var actionIndex = 0; actionIndex < model.Actions.Count; actionIndex++)
        {
            ValidateAuthorizations(model, model.Actions[actionIndex], actionIndex, fieldsByEntity, modelPath);
        }
    }

    private static void ValidateAuthorizations(BeverModel model, ActionModel action, int actionIndex, Dictionary<string, Dictionary<string, FieldModel>> fieldsByEntity, string modelPath)
    {
        for (var index = 0; index < action.Authorizations.Count; index++)
        {
            var authorization = action.Authorizations[index];
            var path = $"actions[{actionIndex}].authorizations[{index}]";
            if (authorization.Kind == "Role")
            {
                if (authorization.Path is not null || !model.Roles.Any(role => role.Name == authorization.Role))
                {
                    Fail(modelPath, path, "role authorization must name a declared role and no path");
                }

                continue;
            }

            if (authorization.Role is not null)
            {
                Fail(modelPath, path, "only Role authorization may name a role");
            }

            switch (authorization.Kind)
            {
                case "Anyone":
                case "Authenticated":
                    if (authorization.Path is not null)
                    {
                        Fail(modelPath, path, "authorization shape is invalid");
                    }
                    break;
                case "Path":
                    if (authorization.Path?.Segments is not { Count: 1 })
                    {
                        Fail(modelPath, path, "only direct single-segment Identity path authorization is supported");
                    }
                    if (fieldsByEntity.TryGetValue(action.Entity, out var fields) && fields.TryGetValue(authorization.Path.Segments[0], out var field) && field.Type is { Kind: "Relationship", TargetKind: "SystemIdentity" })
                    {
                        break;
                    }
                    Fail(modelPath, path, "path authorization must target a local Identity relationship field");
                    break;
                default:
                    Fail(modelPath, path, $"unsupported authorization kind '{authorization.Kind}'");
                    break;
            }
        }
    }

    private static void ValidateActionSearchBy(BeverModel model, Dictionary<string, Dictionary<string, FieldModel>> fieldsByEntity, string modelPath)
    {
        for (var actionIndex = 0; actionIndex < model.Actions.Count; actionIndex++)
        {
            var action = model.Actions[actionIndex];
            if (action.SearchBy is not null)
            {
                if (action.Verb is not ("Read" or "Pick"))
                {
                    Fail(modelPath, $"actions[{actionIndex}].searchBy", "searchBy is valid only on Read and Pick actions");
                }

                if (action.Verb == "Read" && model.Actions.Any(candidate => candidate is { Verb: "Pick", Name: null } && candidate.Entity == action.Entity))
                {
                    Fail(modelPath, $"actions[{actionIndex}].searchBy", "searchBy on Read is unused when a default Pick is declared");
                }

                if (action.SearchBy.Count == 0)
                {
                    Fail(modelPath, $"actions[{actionIndex}].searchBy", "searchBy cannot be empty");
                }

                var fields = fieldsByEntity[action.Entity];
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var searchIndex = 0; searchIndex < action.SearchBy.Count; searchIndex++)
                {
                    var name = ValidateReference(action.SearchBy[searchIndex], fields.Keys, $"actions[{actionIndex}].searchBy[{searchIndex}]", modelPath);
                    AddUnique(seen, name, $"actions[{actionIndex}].searchBy[{searchIndex}]", modelPath);
                    if (!IsSearchable(fields[name].Type))
                    {
                        Fail(modelPath, $"actions[{actionIndex}].searchBy[{searchIndex}]", "search field does not have an allowed searchable type");
                    }
                }
            }
        }
    }

    private static void ValidatePresentation(BeverModel model, EntityModel entity, Dictionary<string, FieldModel> fields, string entityPath, string modelPath)
    {
        var presentation = entity.Presentation!;
        var path = $"{entityPath}.presentation";
        var recordLabel = ValidateReference(presentation.RecordLabel, fields.Keys, $"{path}.recordLabel", modelPath, optional: true);
        if (recordLabel.Length != 0)
        {
            var type = fields[recordLabel].Type;
            var valid = type?.Kind == "Enum" ||
                        (type?.Kind == "Primitive" && RecordLabelPrimitiveTypes.Contains(type.Name ?? string.Empty));
            if (!valid)
            {
                Fail(modelPath, $"{path}.recordLabel", "record label field does not have an allowed display type");
            }
        }
        ValidatePresentationFields(presentation.Fields, fields, $"{path}.fields", modelPath);
        ValidateSurface(presentation.Listing, fields, $"{path}.listing", modelPath);
        ValidateSurface(presentation.Details, fields, $"{path}.details", modelPath);
        ValidateInputExperience(model, entity, presentation.Creating, fields, $"{path}.creating", modelPath, creating: true);
        ValidateInputExperience(model, entity, presentation.Editing, fields, $"{path}.editing", modelPath, creating: false);
        ValidateActionOverrides(model, entity, presentation.ActionOverrides, $"{path}.actionOverrides", modelPath);
    }

    private static void ValidatePresentationFields(List<PresentationFieldModel>? presentationFields, Dictionary<string, FieldModel> fields, string path, string modelPath)
    {
        if (presentationFields is null)
        {
            Fail(modelPath, path, "presentation fields cannot be null");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < presentationFields.Count; index++)
        {
            var field = presentationFields[index];
            if (field is null || field.Label is null)
            {
                Fail(modelPath, $"{path}[{index}]", "field presentation label is required");
            }

            var name = ValidateReference(field.Field, fields.Keys, $"{path}[{index}].field", modelPath);
            AddUnique(seen, name, $"{path}[{index}].field", modelPath);
        }
    }

    private static void ValidateSurface(PresentationSurfaceModel? surface, Dictionary<string, FieldModel> fields, string path, string modelPath)
    {
        if (surface?.Fields is not null)
        {
            ValidateReferenceList(surface.Fields, fields.Keys, $"{path}.fields", modelPath);
        }
    }

    private static void ValidateInputExperience(
        BeverModel model,
        EntityModel entity,
        InputExperiencePresentationModel? experience,
        Dictionary<string, FieldModel> fields,
        string path,
        string modelPath,
        bool creating)
    {
        if (experience is null)
        {
            return;
        }

        var validMode = experience.Mode is "Page" or "Modal" or "None" || (!creating && experience.Mode == "Inline");
        if (!validMode)
        {
            Fail(modelPath, $"{path}.mode", $"invalid {(creating ? "Creating" : "Editing")} mode '{experience.Mode}'");
        }

        var requiredVerb = creating ? "Create" : "Update";
        if (experience.Mode != "None" && !HasAction(model, entity.Name, requiredVerb))
        {
            Fail(modelPath, $"{path}.mode", $"mode '{experience.Mode}' requires action '{requiredVerb}'");
        }

        HashSet<string> projectedFields;
        if (experience.Fields is not null)
        {
            projectedFields = ValidateReferenceList(experience.Fields, fields.Keys, $"{path}.fields", modelPath);
        }
        else
        {
            projectedFields = fields.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (experience.Inputs is null)
        {
            Fail(modelPath, $"{path}.inputs", "inputs cannot be null");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < experience.Inputs.Count; index++)
        {
            var input = experience.Inputs[index];
            if (input is null || input.Widget != "ReferencePicker" || !PickerDisplays.Contains(input.Display))
            {
                Fail(modelPath, $"{path}.inputs[{index}]", "input widget/display must be ReferencePicker with Dropdown, Combobox, or Modal display");
            }

            var name = ValidateReference(input.Field, fields.Keys, $"{path}.inputs[{index}].field", modelPath);
            AddUnique(seen, name, $"{path}.inputs[{index}].field", modelPath);
            if (!projectedFields.Contains(name))
            {
                Fail(modelPath, $"{path}.inputs[{index}].field", "input field is not in the experience field projection");
            }

            var fieldType = fields[name].Type;
            if (fieldType?.Kind != "Relationship" || fieldType.Targets is null || fieldType.Targets.Count != 1)
            {
                Fail(modelPath, $"{path}.inputs[{index}]", "Picker requires a single-target relationship field");
            }

            var target = fieldType.Targets[0];
            if (fieldType.TargetKind == "SystemIdentity" && target == "Identity" && model.Identity.Enabled)
            {
                continue;
            }

            if (fieldType.TargetKind != "Application" || !model.Entities.Any(candidate => candidate is not null && candidate.Name == target) || (fieldType.Picker is null && !IsPickable(model, target)))
            {
                Fail(modelPath, $"{path}.inputs[{index}]", $"Picker target '{target}' must declare Pick or Read");
            }
        }
    }

    private static bool IsSearchable(FieldTypeModel? type) =>
        type?.Kind == "Enum" || (type?.Kind == "Primitive" && SearchablePrimitiveTypes.Contains(type.Name ?? string.Empty));

    private static void ValidateActionOverrides(BeverModel model, EntityModel entity, List<ActionPresentationOverrideModel>? overrides, string path, string modelPath)
    {
        if (overrides is null)
        {
            Fail(modelPath, path, "action overrides cannot be null");
        }

        var declaredActions = model.Actions
            .Where(action => action is not null && string.Equals(action.Entity, entity.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < overrides.Count; index++)
        {
            var actionOverride = overrides[index];
            var actionName = actionOverride?.Action?.Name;
            var declaredAction = declaredActions.FirstOrDefault(action =>
                action.Verb != "Pick" && string.Equals(action.Verb, actionName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(actionName) || declaredAction is null)
            {
                Fail(modelPath, $"{path}[{index}].action", $"action reference '{actionName}' is not declared for entity '{entity.Name}'");
            }

            AddUnique(seen, declaredAction.Verb, $"{path}[{index}].action", modelPath);
            if (actionOverride!.Emphasis is not null && !Emphases.Contains(actionOverride.Emphasis))
            {
                Fail(modelPath, $"{path}[{index}].emphasis", $"invalid emphasis '{actionOverride.Emphasis}'");
            }

            if (actionOverride.Placements is not null)
            {
                if (actionOverride.Placements.Count == 0)
                {
                    Fail(modelPath, $"{path}[{index}].placements", "placements cannot be empty");
                }

                var placements = new HashSet<string>(StringComparer.Ordinal);
                for (var placementIndex = 0; placementIndex < actionOverride.Placements.Count; placementIndex++)
                {
                    var placement = actionOverride.Placements[placementIndex];
                    if (string.IsNullOrWhiteSpace(placement) || !Placements.Contains(placement))
                    {
                        Fail(modelPath, $"{path}[{index}].placements[{placementIndex}]", $"invalid placement '{placement}'");
                    }

                    if (!IsPlacementAllowed(declaredAction.Verb, placement))
                    {
                        Fail(modelPath, $"{path}[{index}].placements[{placementIndex}]", $"placement '{placement}' is not valid for action '{declaredAction.Verb}'");
                    }

                    AddUnique(placements, placement, $"{path}[{index}].placements[{placementIndex}]", modelPath);
                }
            }
        }
    }

    private static HashSet<string> ValidateReferenceList(List<ReferencePathModel> references, IEnumerable<string> fields, string path, string modelPath)
    {
        if (references.Count == 0)
        {
            Fail(modelPath, path, "field projection cannot be empty");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < references.Count; index++)
        {
            var name = ValidateReference(references[index], fields, $"{path}[{index}]", modelPath);
            AddUnique(seen, name, $"{path}[{index}]", modelPath);
        }

        return seen;
    }

    private static string ValidateReference(ReferencePathModel? reference, IEnumerable<string> fields, string path, string modelPath, bool optional = false)
    {
        if (reference is null && optional)
        {
            return string.Empty;
        }

        if (reference is null || reference.Segments is null || reference.Segments.Count != 1 ||
            !fields.Contains(reference.Segments[0], StringComparer.Ordinal))
        {
            Fail(modelPath, path, "reference must be a single-segment path naming one field on the presented entity");
        }

        return reference.Segments[0];
    }

    private static void AddUnique(HashSet<string> values, string value, string path, string modelPath)
    {
        if (!values.Add(value))
        {
            Fail(modelPath, path, $"duplicate value '{value}'");
        }
    }

    private static bool HasAction(BeverModel model, string entity, string verb) =>
        model.Actions.Any(action => action is not null &&
            string.Equals(action.Entity, entity, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(action.Verb, verb, StringComparison.OrdinalIgnoreCase));

    private static bool IsPlacementAllowed(string action, string placement) => action switch
    {
        "Create" => placement == "ListingToolbar",
        "Read" => placement is "Navigation" or "ListingRow" or "DetailsToolbar",
        "Update" or "Delete" => placement is "ListingRow" or "DetailsToolbar",
        _ => false
    };

    [DoesNotReturn]
    private static void Fail(string modelPath, string path, string message) =>
        throw new InvalidOperationException($"Invalid Bever semantic model at {modelPath}:{path}: {message}.");
}
