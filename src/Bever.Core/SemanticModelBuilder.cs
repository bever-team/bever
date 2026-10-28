using Bever.Model;

namespace Bever.Core;

public sealed class SemanticModelBuilder
{
    private static readonly HashSet<string> PrimitiveTypes = new(StringComparer.Ordinal)
    {
        "Text", "Boolean", "Int", "Decimal", "Money", "Date", "DateTime", "Time", "Email", "Phone", "Url"
    };

    // Reserved names from the language draft that are not implemented yet.
    private static readonly HashSet<string> PlannedTypes = new(StringComparer.Ordinal) { "Duration", "File", "Image", "Multiple" };

    private static readonly HashSet<string> SystemFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "CreatedOn", "CreatedBy", "ModifiedOn", "ModifiedBy", "DeletedOn", "DeletedBy"
    };

    private readonly DiagnosticBag diagnostics;

    // Entities each source file may reference: the ones it declares plus the ones it imports.
    private readonly Dictionary<string, HashSet<string>> visibleEntities = new(StringComparer.Ordinal);

    // Declared roles by name, ignoring case. Roles are model-wide and need no import.
    private readonly Dictionary<string, RoleModel> roles = new(StringComparer.OrdinalIgnoreCase);

    // Subjects of 'By' and built-in account kinds; a role with one of these names would be ambiguous.
    private static readonly HashSet<string> ReservedRoleNames = new(StringComparer.OrdinalIgnoreCase) { "Anyone", "Authenticated", "Administrator", "Identity" };

    public SemanticModelBuilder(DiagnosticBag diagnostics)
    {
        this.diagnostics = diagnostics;
    }

    public BeverModel Build(IReadOnlyList<CompilationUnitSyntax> syntaxTrees)
    {
        var entities = new List<EntityModel>();
        var entityNames = new HashSet<string>(StringComparer.Ordinal);
        var entitySyntaxes = new List<(EntityDeclarationSyntax Syntax, EntityModel Model)>();
        var identitySyntaxes = new List<IdentityDeclarationSyntax>();
        var roleSyntaxes = new List<RoleDeclarationSyntax>();
        var actionSyntaxes = new List<ActionDeclarationSyntax>();
        var entityRoutes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var syntaxTree in syntaxTrees)
        {
            foreach (var member in syntaxTree.Members)
            {
                if (member is ActionDeclarationSyntax actionSyntax)
                {
                    actionSyntaxes.Add(actionSyntax);
                    continue;
                }

                if (member is IdentityDeclarationSyntax identitySyntax)
                {
                    identitySyntaxes.Add(identitySyntax);
                    continue;
                }

                if (member is RoleDeclarationSyntax roleSyntax)
                {
                    roleSyntaxes.Add(roleSyntax);
                    continue;
                }

                if (member is not EntityDeclarationSyntax entitySyntax)
                {
                    continue;
                }

                if (!IsIdentifier(entitySyntax.Name))
                {
                    diagnostics.Report("BEV1004", DiagnosticSeverity.Error, $"Entity name '{entitySyntax.Name}' must start with a letter and contain only letters and digits.", entitySyntax.Span);
                    continue;
                }

                if (!entityNames.Add(entitySyntax.Name))
                {
                    diagnostics.Report("BEV1001", DiagnosticSeverity.Error, $"Duplicate entity '{entitySyntax.Name}'.", entitySyntax.Span);
                    continue;
                }

                if (string.Equals(entitySyntax.Name, "Identity", StringComparison.Ordinal))
                {
                    diagnostics.Report("BEV1003", DiagnosticSeverity.Error, "Entity name 'Identity' is reserved for the built-in identity target.", entitySyntax.Span);
                    entityNames.Remove(entitySyntax.Name);
                    continue;
                }

                var route = ToGeneratedEntityRouteName(entitySyntax.Name);
                if (entityRoutes.TryGetValue(route, out var existing))
                {
                    diagnostics.Report("BEV1005", DiagnosticSeverity.Error, $"Entities '{existing}' and '{entitySyntax.Name}' would share the HTTP route '/{route}'. Rename one of them.", entitySyntax.Span);
                    entityNames.Remove(entitySyntax.Name);
                    continue;
                }

                entityRoutes[route] = entitySyntax.Name;
                var entity = BuildEntity(entitySyntax);
                entities.Add(entity);
                entitySyntaxes.Add((entitySyntax, entity));
            }
        }

        ResolveImports(syntaxTrees, entitySyntaxes.Select(item => item.Syntax).ToList());
        var identity = BuildIdentity(identitySyntaxes);
        BuildRoles(roleSyntaxes, identity.Enabled);

        var entitiesByName = entities.ToDictionary(entity => entity.Name, StringComparer.Ordinal);
        ValidateRelationshipTargets(entities, entitiesByName, identity.Enabled);
        var actions = BuildActions(actionSyntaxes, entitiesByName, entityNames, identity.Enabled);
        ValidatePickers(entities, actions);
        WarnWhenCreatorsMayNotSeeTheirRecords(actions);
        var presentationBuilder = new PresentationSemanticBuilder(diagnostics, entitiesByName, actions);
        foreach (var (syntax, model) in entitySyntaxes)
        {
            model.Presentation = presentationBuilder.Build(syntax, model);
        }

        ValidatePickableForms(entities, actions);
        ValidateTargetLabels(entities);
        return new BeverModel(BeverModel.LanguageVersion, BeverModel.ModelVersion, identity, entities, actions, roles.Values.ToList());
    }

    private void ResolveImports(IReadOnlyList<CompilationUnitSyntax> syntaxTrees, IReadOnlyList<EntityDeclarationSyntax> declarations)
    {
        var files = syntaxTrees.Select(tree => tree.Span.Start.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var tree in syntaxTrees)
        {
            var file = tree.Span.Start.Path;
            var visible = declarations.Where(declaration => declaration.Span.Start.Path == file).Select(declaration => declaration.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var import in tree.Members.OfType<ImportDeclarationSyntax>())
            {
                var target = ResolveImportPath(file, import.Path);
                if (!files.Contains(target))
                {
                    diagnostics.Report("BEV1020", DiagnosticSeverity.Error, $"Import path '{import.Path}' does not name a .bev file of this model.", import.Span);
                    continue;
                }

                foreach (var name in import.Names)
                {
                    var declaration = declarations.FirstOrDefault(candidate => candidate.Name == name && candidate.Span.Start.Path == target);
                    if (declaration is not { Exported: true })
                    {
                        diagnostics.Report("BEV1021", DiagnosticSeverity.Error, declaration is null
                            ? $"'{import.Path}' declares no entity '{name}'."
                            : $"'{name}' is not exported. Declare it as 'Export Entity {name}' in '{import.Path}'.", import.Span);
                        continue;
                    }

                    if (!visible.Add(name))
                    {
                        diagnostics.Report("BEV1022", DiagnosticSeverity.Error, $"'{name}' is already declared or imported in this file.", import.Span);
                    }
                }
            }

            visibleEntities[file] = visible;
        }
    }

    // Source paths use '/' separators; an import path is relative to the importing file.
    private static string ResolveImportPath(string importer, string path)
    {
        var segments = importer.Split('/').SkipLast(1).ToList();
        foreach (var segment in path.Split('/'))
        {
            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return string.Empty;
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else if (segment is not ("." or ""))
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    private static string RelativeImportPath(string importer, string target)
    {
        var from = importer.Split('/').SkipLast(1).ToList();
        var to = target.Split('/').ToList();
        var common = 0;
        while (common < from.Count && common < to.Count - 1 && from[common] == to[common])
        {
            common++;
        }

        return string.Join('/', Enumerable.Repeat("..", from.Count - common).Concat(to.Skip(common)));
    }

    // An entity declared in another file is usable only once that file exports it and this file imports it.
    private void ReportWhenNotVisible(string entityName, SourceSpan span, IReadOnlyDictionary<string, EntityModel> entitiesByName)
    {
        if (visibleEntities.TryGetValue(span.Start.Path, out var visible) && visible.Contains(entityName))
        {
            return;
        }

        var declaration = entitiesByName[entityName].SourceSpan!.Start.Path;
        diagnostics.Report("BEV1023", DiagnosticSeverity.Error, $"Entity '{entityName}' is declared in '{declaration}'. Export it there and add 'Import {entityName} From \"{RelativeImportPath(span.Start.Path, declaration)}\"' to use it here.", span);
    }

    private IdentityModel BuildIdentity(IReadOnlyList<IdentityDeclarationSyntax> declarations)
    {
        if (declarations.Count == 0)
        {
            return IdentityModel.Disabled();
        }

        foreach (var duplicate in declarations.Skip(1))
        {
            diagnostics.Report("BEV5001", DiagnosticSeverity.Error, "Duplicate Identity declaration.", duplicate.Span);
        }

        var registration = false;
        var seenRegistration = false;
        var seenSummary = false;
        foreach (var member in declarations[0].Members)
        {
            if (member.Text == "Registration Enabled")
            {
                if (seenRegistration)
                {
                    diagnostics.Report("BEV5002", DiagnosticSeverity.Error, "Duplicate Identity Registration declaration.", member.Span);
                }
                seenRegistration = true;
                registration = true;
                continue;
            }

            if (member.Text == "Summary Label Email")
            {
                if (seenSummary)
                {
                    diagnostics.Report("BEV5003", DiagnosticSeverity.Error, "Duplicate Identity Summary declaration.", member.Span);
                }
                seenSummary = true;
                continue;
            }

            diagnostics.Report("BEV5004", DiagnosticSeverity.Error, $"Unsupported Identity member '{member.Text}'.", member.Span);
        }

        return new IdentityModel(
            true,
            registration
                ? ["Login", "Logout", "Session", "Register"]
                : ["Login", "Logout", "Session"],
            new IdentitySummaryModel("Email"),
            [new IdentitySystemTargetModel("Identity", "SystemIdentity", new IdentityLookupModel("IdLabel", true, false))]);
    }

    private void BuildRoles(IReadOnlyList<RoleDeclarationSyntax> declarations, bool identityEnabled)
    {
        foreach (var declaration in declarations)
        {
            if (!IsIdentifier(declaration.Name))
            {
                diagnostics.Report("BEV5009", DiagnosticSeverity.Error, declaration.Name.Length == 0
                    ? "Role needs a name, for example 'Role Agent'."
                    : $"Role name '{declaration.Name}' must start with a letter and contain only letters and digits.", declaration.Span);
                continue;
            }

            if (ReservedRoleNames.Contains(declaration.Name))
            {
                diagnostics.Report("BEV5009", DiagnosticSeverity.Error, $"'{declaration.Name}' is reserved and cannot be a role name.", declaration.Span);
                continue;
            }

            if (!identityEnabled)
            {
                diagnostics.Report("BEV5007", DiagnosticSeverity.Error, $"Role '{declaration.Name}' requires a top-level Identity declaration.", declaration.Span);
            }

            if (!roles.TryAdd(declaration.Name, new RoleModel(declaration.Name, declaration.Span)))
            {
                diagnostics.Report("BEV5008", DiagnosticSeverity.Error, $"Duplicate role '{declaration.Name}'.", declaration.Span);
            }
        }
    }

    private void ValidateRelationshipTargets(IEnumerable<EntityModel> entities, IReadOnlyDictionary<string, EntityModel> entitiesByName, bool identityEnabled)
    {
        foreach (var field in entities.SelectMany(entity => entity.Fields))
        {
            if (field.Type?.Kind != "Relationship")
            {
                continue;
            }

            var hasIdentityTarget = field.Type.Targets.Any(target => target == "Identity");
            if (hasIdentityTarget && !identityEnabled)
            {
                diagnostics.Report("BEV3000", DiagnosticSeverity.Error, "Relationship target 'Identity' requires a top-level Identity declaration.", field.SourceSpan!);
            }

            if (hasIdentityTarget && field.Type.Targets.Count != 1)
            {
                diagnostics.Report("BEV3022", DiagnosticSeverity.Error, "Relationship target 'Identity' cannot be mixed with application targets.", field.SourceSpan!);
            }

            field.Type.TargetKind = hasIdentityTarget ? "SystemIdentity" : "Application";
            foreach (var target in field.Type.Targets.Where(target => target != "Identity"))
            {
                if (!entitiesByName.ContainsKey(target))
                {
                    diagnostics.Report("BEV3001", DiagnosticSeverity.Error, $"Relationship target '{target}' is not a declared entity.", field.SourceSpan!);
                    continue;
                }

                ReportWhenNotVisible(target, field.SourceSpan!, entitiesByName);
            }
        }
    }

    private EntityModel BuildEntity(EntityDeclarationSyntax syntax)
    {
        var fields = new List<FieldModel>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in syntax.Members.OfType<FieldDeclarationSyntax>())
        {
            if (!IsIdentifier(field.Name))
            {
                diagnostics.Report("BEV1010", DiagnosticSeverity.Error, $"Field name '{field.Name}' must start with a letter and contain only letters and digits.", field.Span);
                continue;
            }

            if (SystemFieldNames.Contains(field.Name))
            {
                diagnostics.Report("BEV1011", DiagnosticSeverity.Error, $"'{field.Name}' is a system field that every entity already has. Choose another name.", field.Span);
                continue;
            }

            if (!names.Add(field.Name))
            {
                diagnostics.Report("BEV1012", DiagnosticSeverity.Error, $"Duplicate field '{field.Name}' on entity '{syntax.Name}'.", field.Span);
                continue;
            }

            var (typeText, defaultText) = SplitDefaultValue(field.TypeText);
            var type = ParseFieldType(typeText, field.Span);
            var defaultValue = type is null || defaultText is null ? null : ParseDefaultValue(defaultText, type, field.Name, field.Span);
            fields.Add(new FieldModel(field.Name, type, defaultValue, field.Span));
        }

        var entity = new EntityModel(syntax.Name, syntax.Span, fields);
        foreach (var rule in syntax.Members.OfType<RuleDeclarationSyntax>())
        {
            if (RuleSemanticBuilder.Build(rule, entity, diagnostics) is { } model)
            {
                entity.Rules.Add(model);
            }
        }

        return entity;
    }

    private List<ActionModel> BuildActions(
        IReadOnlyList<ActionDeclarationSyntax> actionSyntaxes,
        IReadOnlyDictionary<string, EntityModel> entitiesByName,
        HashSet<string> entityNames,
        bool identityEnabled)
    {
        var actions = new List<ActionModel>();
        var actionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var actionSyntax in actionSyntaxes)
        {
            var header = actionSyntax.Text;
            string? pickerName = null;
            var asIndex = header.LastIndexOf(" As ", StringComparison.Ordinal);
            if (asIndex > 0)
            {
                pickerName = header[(asIndex + " As ".Length)..].Trim();
                header = header[..asIndex].TrimEnd();
            }

            var lastSpaceIndex = header.LastIndexOf(' ');
            if (lastSpaceIndex <= 0 || lastSpaceIndex == header.Length - 1)
            {
                diagnostics.Report("BEV2001", DiagnosticSeverity.Error, "Action requires a verb and entity.", actionSyntax.Span);
                continue;
            }

            var verbText = header[..lastSpaceIndex].Trim();
            var entityName = header[(lastSpaceIndex + 1)..].Trim();
            if (pickerName is not null && (verbText != "Pick" || !IsIdentifier(pickerName)))
            {
                diagnostics.Report("BEV2006", DiagnosticSeverity.Error, "'As <Name>' names a picker: it is valid only as 'Action Pick <Entity> As <Name>' with an identifier name.", actionSyntax.Span);
                continue;
            }

            if (entityName == "Identity")
            {
                diagnostics.Report("BEV2005", DiagnosticSeverity.Error, "Identity is a reserved system target and does not support ordinary actions.", actionSyntax.Span);
                continue;
            }
            if (!entityNames.Contains(entityName))
            {
                diagnostics.Report("BEV2002", DiagnosticSeverity.Error, $"Action references unknown entity '{entityName}'.", actionSyntax.Span);
                continue;
            }

            ReportWhenNotVisible(entityName, actionSyntax.Span, entitiesByName);

            // Clauses are checked once per declaration and apply to the verbs that accept them,
            // so 'Action Crud Task' may carry OrderBy and SearchBy for its Read.
            var verbs = ExpandActionVerbs(verbText, actionSyntax.Span).ToList();
            if (!actionSyntax.Members.OfType<AuthorizationClauseSyntax>().Any())
            {
                diagnostics.Report("BEV2007", DiagnosticSeverity.Error, $"Action '{verbText} {entityName}' must say who may perform it, for example 'By Authenticated'.", actionSyntax.Span);
            }

            var orderBy = BuildActionOrderBy(actionSyntax, entitiesByName[entityName]);
            if (orderBy is not null && !verbs.Contains("Read"))
            {
                diagnostics.Report(
                    verbs.Contains("Pick") ? "BEV3023" : "BEV3029",
                    DiagnosticSeverity.Error,
                    verbs.Contains("Pick") ? "OrderBy is not valid under Action Pick; picker order is fixed by label." : $"OrderBy is valid only under Action Read, not '{verbText}'.",
                    actionSyntax.Span);
            }

            var searchBy = BuildActionSearchBy(actionSyntax, verbs, entitiesByName[entityName]);
            var authorizations = BuildActionAuthorizations(actionSyntax, entitiesByName[entityName], identityEnabled);
            foreach (var verb in verbs)
            {
                var actionKey = $"{verb}:{entityName}:{pickerName}";
                if (!actionKeys.Add(actionKey))
                {
                    diagnostics.Report("BEV2004", DiagnosticSeverity.Error, $"Duplicate action '{verb} {entityName}{(pickerName is null ? "" : $" As {pickerName}")}'.", actionSyntax.Span);
                    continue;
                }

                actions.Add(new ActionModel(
                    verb,
                    entityName,
                    actionSyntax.Span,
                    authorizations,
                    verb == "Read" ? orderBy : null,
                    verb is "Read" or "Pick" ? searchBy : null,
                    pickerName));
            }
        }

        return actions;
    }

    private IReadOnlyList<AuthorizationModel> BuildActionAuthorizations(ActionDeclarationSyntax actionSyntax, EntityModel entity, bool identityEnabled) =>
        actionSyntax.Members
            .OfType<AuthorizationClauseSyntax>()
            .Select(authorization => ParseAuthorization(authorization.Text, authorization.Span, entity, identityEnabled))
            .OfType<AuthorizationModel>()
            .ToList();

    private OrderByModel? BuildActionOrderBy(ActionDeclarationSyntax actionSyntax, EntityModel entity)
    {
        var clauses = actionSyntax.Members.OfType<OrderByClauseSyntax>().ToList();
        foreach (var duplicate in clauses.Skip(1))
        {
            diagnostics.Report("BEV3030", DiagnosticSeverity.Error, "Duplicate OrderBy declaration.", duplicate.Span);
        }

        return clauses.Count == 0 ? null : ParseOrderBy(clauses[0].Text, entity, clauses[0].Span);
    }

    private IReadOnlyList<ReferencePathModel>? BuildActionSearchBy(
        ActionDeclarationSyntax actionSyntax,
        IReadOnlyList<string> verbs,
        EntityModel entity)
    {
        var clauses = actionSyntax.Members.OfType<SearchByClauseSyntax>().ToList();
        if (clauses.Count == 0)
        {
            return null;
        }

        foreach (var duplicateClause in clauses.Skip(1))
        {
            diagnostics.Report("BEV3018", DiagnosticSeverity.Error, "Duplicate SearchBy declaration.", duplicateClause.Span);
        }

        if (!verbs.Any(verb => verb is "Read" or "Pick"))
        {
            diagnostics.Report("BEV3014", DiagnosticSeverity.Error, "SearchBy is valid only under Action Read or Action Pick.", clauses[0].Span);
            return null;
        }

        return ParseSearchBy(clauses[0].Text, entity, clauses[0].Span);
    }

    private IReadOnlyList<ReferencePathModel> ParseSearchBy(string text, EntityModel entity, SourceSpan span)
    {
        var values = text.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length == 0 || values.Any(value => value.Length == 0))
        {
            diagnostics.Report("BEV3015", DiagnosticSeverity.Error, "SearchBy needs at least one field, for example 'SearchBy Name'.", span);
            return [];
        }

        var result = new List<ReferencePathModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var fieldName = ParseFieldName(value, diagnostics, span);
            if (fieldName is null)
            {
                continue;
            }

            if (!seen.Add(fieldName))
            {
                diagnostics.Report("BEV3018", DiagnosticSeverity.Error, $"Duplicate SearchBy field '{fieldName}'.", span);
                continue;
            }

            var field = entity.Fields.FirstOrDefault(item => item.Name == fieldName);
            if (field is null)
            {
                diagnostics.Report("BEV3017", DiagnosticSeverity.Error, $"SearchBy references unknown field '{fieldName}' on entity '{entity.Name}'.", span);
                continue;
            }

            if (!IsSearchable(field.Type))
            {
                diagnostics.Report("BEV3019", DiagnosticSeverity.Error, $"SearchBy field '{fieldName}' does not have a supported searchable type.", span);
                continue;
            }

            result.Add(new ReferencePathModel([fieldName]));
        }

        return result;
    }

    private FieldTypeModel? ParseFieldType(string? text, SourceSpan span)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            diagnostics.Report("BEV0006", DiagnosticSeverity.Error, "Field requires a type.", span);
            return null;
        }

        text = text.Trim();
        if (text.StartsWith("->", StringComparison.Ordinal))
        {
            return ParseRelationshipType(text[2..], span);
        }

        var nullable = text.EndsWith('?');
        if (nullable)
        {
            text = text[..^1].TrimEnd();
        }

        var head = text.Split('(', ' ')[0];
        if (PlannedTypes.Contains(head))
        {
            diagnostics.Report("BEV3031", DiagnosticSeverity.Error, $"Type '{head}' is not supported yet. See https://bever.dev/roadmap.", span);
            return null;
        }

        var name = text;
        string? argumentText = null;
        if (TryParseCall(text, out var callName, out var callArguments, out var remainder))
        {
            if (remainder.Length > 0)
            {
                diagnostics.Report("BEV3003", DiagnosticSeverity.Error, $"Unsupported field type syntax '{text}'.", span);
                return null;
            }

            name = callName;
            argumentText = callArguments;
        }
        else if (text.Contains('(') || text.Contains(')') || text.Contains(' '))
        {
            diagnostics.Report("BEV3003", DiagnosticSeverity.Error, $"Unsupported field type syntax '{text}'.", span);
            return null;
        }

        if (name == "Enum")
        {
            var options = ParseEnumOptions(argumentText ?? string.Empty, span);
            return options is null ? null : new FieldTypeModel("Enum", null, nullable, [], options, []);
        }

        if (!PrimitiveTypes.Contains(name))
        {
            diagnostics.Report("BEV3032", DiagnosticSeverity.Error, $"Unknown type '{name}'. Use Text, Boolean, Int, Decimal, Money, Date, DateTime, Time, Email, Phone, Url, Enum(...), or '-> Entity'.", span);
            return null;
        }

        if (argumentText is null)
        {
            return new FieldTypeModel("Primitive", name, nullable, [], [], []);
        }

        if (name != "Text")
        {
            diagnostics.Report("BEV3033", DiagnosticSeverity.Error, $"Type '{name}' does not take arguments.", span);
            return null;
        }

        if (!int.TryParse(argumentText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var maxLength) || maxLength < 1)
        {
            diagnostics.Report("BEV3004", DiagnosticSeverity.Error, "Text length must be a positive integer, for example Text(200).", span);
            return null;
        }

        return new FieldTypeModel("Primitive", "Text", nullable, [new Dictionary<string, object?> { ["maxLength"] = maxLength }], [], []);
    }

    private FieldTypeModel? ParseRelationshipType(string text, SourceSpan span)
    {
        // Trailing clauses 'OnDelete <Behavior>' and 'Pick <Picker>' may appear in any order, each at most once.
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var clauseStart = Array.FindIndex(tokens, token => token is "OnDelete" or "Pick");
        if (clauseStart < 0)
        {
            clauseStart = tokens.Length;
        }

        string? onDeleteText = null;
        string? picker = null;
        for (var index = clauseStart; index < tokens.Length; index += 2)
        {
            var keyword = tokens[index];
            var duplicate = keyword == "OnDelete" ? onDeleteText is not null : picker is not null;
            if (keyword is not ("OnDelete" or "Pick") || index + 1 >= tokens.Length || duplicate)
            {
                diagnostics.Report("BEV3024", DiagnosticSeverity.Error, "Relationship clauses are 'OnDelete <Behavior>' and 'Pick <Picker>', each at most once.", span);
                return null;
            }

            if (keyword == "OnDelete")
            {
                onDeleteText = tokens[index + 1];
            }
            else
            {
                picker = tokens[index + 1];
            }
        }

        var targetText = string.Join(' ', tokens[..clauseStart]);
        var nullable = targetText.EndsWith('?');
        if (nullable)
        {
            targetText = targetText[..^1].TrimEnd();
        }

        if (targetText.Length == 0)
        {
            diagnostics.Report("BEV3001", DiagnosticSeverity.Error, "Relationship field requires a target entity.", span);
            return null;
        }

        if (targetText.Contains(" Or ", StringComparison.Ordinal))
        {
            diagnostics.Report("BEV3031", DiagnosticSeverity.Error, "Relationships to more than one entity ('A Or B') are not supported yet. See https://bever.dev/roadmap.", span);
            return null;
        }

        if (!IsIdentifier(targetText))
        {
            diagnostics.Report("BEV3001", DiagnosticSeverity.Error, $"Relationship target '{targetText}' is not a valid entity name.", span);
            return null;
        }

        var onDelete = onDeleteText ?? "Restrict";
        if (onDelete is not "Restrict" and not "Cascade" and not "SetNull")
        {
            diagnostics.Report("BEV3012", DiagnosticSeverity.Error, $"OnDelete behavior must be Restrict, Cascade, or SetNull, not '{onDelete}'.", span);
            return null;
        }

        if (onDelete == "SetNull" && !nullable)
        {
            diagnostics.Report("BEV3013", DiagnosticSeverity.Error, "OnDelete SetNull requires a nullable relationship.", span);
            return null;
        }

        return new FieldTypeModel("Relationship", null, nullable, [], [], [targetText], onDelete, picker: picker);
    }

    // A value written in source for a field of this type: a default, or the other side of a rule comparison.
    internal static LiteralValueModel? ParseLiteral(string text, FieldTypeModel type)
    {
        var quoted = text.Length >= 2 && text[0] == '"' && text[^1] == '"';
        return (type.Kind, type.Name) switch
        {
            ("Enum", _) => type.Options.FirstOrDefault(option => option.Name == text) is { } option ? new LiteralValueModel("Identifier", option.Name) : null,
            ("Primitive", "Boolean") => text is "True" or "False" ? new LiteralValueModel("Boolean", text == "True") : null,
            ("Primitive", "Int") => int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var integer) ? new LiteralValueModel("Integer", integer) : null,
            ("Primitive", "Decimal" or "Money") => decimal.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var number) ? new LiteralValueModel("Decimal", number) : null,
            ("Primitive", _) when quoted && BeverValueFormats.IsValid(type.Name!, text[1..^1]) => new LiteralValueModel("Text", text[1..^1]),
            _ => null
        };
    }

    private LiteralValueModel? ParseDefaultValue(string text, FieldTypeModel type, string fieldName, SourceSpan span)
    {
        var literal = ParseLiteral(text, type);
        if (literal is null)
        {
            var expected = (type.Kind, type.Name) switch
            {
                ("Enum", _) => $"one of {string.Join(", ", type.Options.Select(option => option.Name))}",
                ("Relationship", _) => null,
                ("Primitive", "Boolean") => "True or False",
                ("Primitive", "Int") => "a whole number",
                ("Primitive", "Decimal" or "Money") => "a number such as 9.99",
                ("Primitive", "Date") => "a quoted date such as \"2026-01-31\"",
                ("Primitive", "DateTime") => "a quoted UTC date and time such as \"2026-01-31T09:00:00Z\"",
                ("Primitive", "Time") => "a quoted time such as \"09:30\"",
                _ => $"a quoted {type.Name} value"
            };
            diagnostics.Report("BEV3034", DiagnosticSeverity.Error, expected is null
                ? $"Relationship field '{fieldName}' cannot have a default value."
                : $"Default value for '{fieldName}' must be {expected}.", span);
            return null;
        }

        if (literal.Value is string value && type.Arguments.FirstOrDefault()?.GetValueOrDefault("maxLength") is int maxLength && value.Length > maxLength)
        {
            diagnostics.Report("BEV3034", DiagnosticSeverity.Error, $"Default value for '{fieldName}' is longer than {maxLength} characters.", span);
            return null;
        }

        return literal;
    }

    private static (string TypeText, string? DefaultText) SplitDefaultValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, null);
        }

        var inString = false;
        var parenthesisDepth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
            {
                inString = !inString;
            }

            if (!inString && text[index] == '(')
            {
                parenthesisDepth++;
            }

            if (!inString && text[index] == ')' && parenthesisDepth > 0)
            {
                parenthesisDepth--;
            }

            if (!inString && parenthesisDepth == 0 && text[index] == '=')
            {
                return (text[..index].Trim(), text[(index + 1)..].Trim());
            }
        }

        return (text.Trim(), null);
    }

    private static bool TryParseCall(string text, out string name, out string argumentText, out string remainder)
    {
        name = string.Empty;
        argumentText = string.Empty;
        remainder = string.Empty;

        var openIndex = text.IndexOf('(');
        if (openIndex <= 0)
        {
            return false;
        }

        var depth = 0;
        for (var index = openIndex; index < text.Length; index++)
        {
            if (text[index] == '(')
            {
                depth++;
            }
            else if (text[index] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    name = text[..openIndex].Trim();
                    argumentText = text[(openIndex + 1)..index].Trim();
                    remainder = text[(index + 1)..].Trim();
                    return name.Length > 0;
                }
            }
        }

        return false;
    }

    private IReadOnlyList<EnumOptionModel>? ParseEnumOptions(string optionText, SourceSpan span)
    {
        var options = new List<EnumOptionModel>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in optionText.Split(',', StringSplitOptions.TrimEntries))
        {
            var parts = option.Split('=', 2, StringSplitOptions.TrimEntries);
            if (!IsIdentifier(parts[0]))
            {
                diagnostics.Report("BEV3035", DiagnosticSeverity.Error, $"Enum option '{option}' must be a name that starts with a letter, for example Enum(Low, Medium, High).", span);
                return null;
            }

            if (!names.Add(parts[0]))
            {
                diagnostics.Report("BEV3035", DiagnosticSeverity.Error, $"Duplicate enum option '{parts[0]}'.", span);
                return null;
            }

            if (parts.Length == 2 && !int.TryParse(parts[1], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                diagnostics.Report("BEV3035", DiagnosticSeverity.Error, $"Enum option value '{parts[1]}' must be a whole number.", span);
                return null;
            }

            options.Add(new EnumOptionModel(parts[0], parts.Length == 2 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : null));
        }

        return options;
    }

    private AuthorizationModel? ParseAuthorization(string text, SourceSpan span, EntityModel entity, bool identityEnabled)
    {
        if (text.Contains(" Where ", StringComparison.Ordinal))
        {
            diagnostics.Report("BEV3005", DiagnosticSeverity.Error, "Conditional rules ('By ... Where ...') are not supported yet. See https://bever.dev/roadmap.", span);
            return null;
        }

        if (text == "Anyone" || text == "Authenticated")
        {
            return new AuthorizationModel(text, null);
        }

        if (text.Length == 0)
        {
            diagnostics.Report("BEV3006", DiagnosticSeverity.Error, "'By' needs a subject: Anyone, Authenticated, a role, or a field.", span);
            return null;
        }

        if (text.Contains('/'))
        {
            ParseFieldName(text, diagnostics, span);
            return null;
        }

        var field = entity.Fields.FirstOrDefault(item => item.Name == text);
        // Like field names, role names in 'By' match exactly; declarations are unique ignoring case.
        var role = roles.TryGetValue(text, out var declared) && declared.Name == text ? declared : null;
        if (field is not null && role is not null)
        {
            diagnostics.Report("BEV3036", DiagnosticSeverity.Error, $"'By {text}' is ambiguous: '{text}' is both a field of '{entity.Name}' and a role. Rename one of them.", span);
            return null;
        }

        if (role is not null)
        {
            return new AuthorizationModel("Role", null, role.Name);
        }

        if (field is null)
        {
            diagnostics.Report("BEV3021", DiagnosticSeverity.Error, $"'By {text}' names no role or field. Use 'By Anyone', 'By Authenticated', 'By <Role>' with a declared 'Role {text}', or 'By <Field>' with a field that references Identity.", span);
            return null;
        }

        if (!identityEnabled || field.Type?.Kind != "Relationship" || field.Type.TargetKind != "SystemIdentity")
        {
            diagnostics.Report("BEV3020", DiagnosticSeverity.Error, $"'By {text}' needs '{text}' to reference Identity, for example 'Field {text} -> Identity', and a top-level Identity block.", span);
            return null;
        }

        return new AuthorizationModel("Path", new ReferencePathModel([text]));
    }

    private OrderByModel? ParseOrderBy(string text, EntityModel entity, SourceSpan span)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2)
        {
            diagnostics.Report("BEV3007", DiagnosticSeverity.Error, "OrderBy requires a field and an optional Asc or Desc direction, for example 'OrderBy DueDate Desc'.", span);
            return null;
        }

        var direction = parts.Length == 2 ? parts[1] : "Asc";
        if (direction is not "Asc" and not "Desc")
        {
            diagnostics.Report("BEV3008", DiagnosticSeverity.Error, "OrderBy direction must be Asc or Desc.", span);
            return null;
        }

        var fieldName = ParseFieldName(parts[0], diagnostics, span);
        if (fieldName is null)
        {
            return null;
        }

        var field = entity.Fields.FirstOrDefault(candidate => candidate.Name == fieldName);
        if (fieldName is "CreatedOn" or "ModifiedOn")
        {
            return new OrderByModel(new ReferencePathModel([fieldName]), direction);
        }

        if (field is null || !IsIdentifier(fieldName))
        {
            diagnostics.Report("BEV3007", DiagnosticSeverity.Error, $"OrderBy references unknown field '{parts[0]}' on entity '{entity.Name}'. Use one of its fields, CreatedOn, or ModifiedOn.", span);
            return null;
        }

        if (field.Type?.Kind is not ("Primitive" or "Enum"))
        {
            diagnostics.Report("BEV3007", DiagnosticSeverity.Error, $"OrderBy cannot sort by relationship field '{fieldName}'.", span);
            return null;
        }

        return new OrderByModel(new ReferencePathModel([fieldName]), direction);
    }

    private static bool IsSearchable(FieldTypeModel? type) =>
        type?.Kind == "Enum" ||
        (type?.Kind == "Primitive" && type.Name is "Text" or "Email" or "Phone" or "Url");

    private static string ToGeneratedEntityRouteName(string entityName) => Pluralize(ToKebabCase(entityName));

    private static string ToKebabCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsWhiteSpace(current) || current == '_' || current == '-')
            {
                AppendDash(builder);
                continue;
            }

            if (char.IsUpper(current) && builder.Length > 0 && builder[^1] != '-')
            {
                var previous = value[index - 1];
                var hasNextLower = index + 1 < value.Length && char.IsLower(value[index + 1]);
                if (char.IsLower(previous) || char.IsDigit(previous) || hasNextLower)
                {
                    builder.Append('-');
                }
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString().Trim('-');
    }

    private static void AppendDash(System.Text.StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '-')
        {
            builder.Append('-');
        }
    }

    private static string Pluralize(string value)
    {
        if (value.EndsWith('y') && value.Length > 1 && !IsVowel(value[^2]))
        {
            return value[..^1] + "ies";
        }

        if (value.EndsWith('s') || value.EndsWith('x') || value.EndsWith('z') || value.EndsWith("ch", StringComparison.Ordinal) || value.EndsWith("sh", StringComparison.Ordinal))
        {
            return value + "es";
        }

        return value + "s";
    }

    private static bool IsVowel(char value) => value is 'a' or 'e' or 'i' or 'o' or 'u';

    // A field path names a field of the entity in scope ('Title'). Paths through relationships ('Owner/Team') are planned.
    internal static string? ParseFieldName(string text, DiagnosticBag diagnostics, SourceSpan span)
    {
        if (text.StartsWith('/'))
        {
            diagnostics.Report("BEV3010", DiagnosticSeverity.Error, $"Field paths have no leading '/'. Write '{text.TrimStart('/')}' instead of '{text}'.", span);
            return null;
        }

        if (text.Contains('/'))
        {
            diagnostics.Report("BEV3031", DiagnosticSeverity.Error, $"Paths through relationships such as '{text}' are not supported yet. See https://bever.dev/roadmap.", span);
            return null;
        }

        return text;
    }

    private void ValidatePickers(IReadOnlyList<EntityModel> entities, IReadOnlyList<ActionModel> actions)
    {
        foreach (var field in entities.SelectMany(entity => entity.Fields))
        {
            if (field.Type is not { Kind: "Relationship", Picker: { } picker } type)
            {
                continue;
            }

            if (type.TargetKind != "Application" || type.Targets.Count != 1)
            {
                diagnostics.Report("BEV3024", DiagnosticSeverity.Error, $"'Pick {picker}' on '{field.Name}' requires a single-target application relationship; Identity picking is bound to the containing Create/Update action.", field.SourceSpan!);
                continue;
            }

            var named = actions.FirstOrDefault(action =>
                action.Verb == "Pick" && action.Entity == type.Targets[0] && string.Equals(action.Name, picker, StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                diagnostics.Report("BEV3024", DiagnosticSeverity.Error, $"'{field.Name}' names unknown picker '{picker}'; declare 'Action Pick {type.Targets[0]} As {picker}'.", field.SourceSpan!);
                continue;
            }

            type.Picker = named.Name;
        }

        foreach (var read in actions.Where(action => action.Verb == "Read" && action.SearchBy is not null))
        {
            if (actions.Any(action => action.Verb == "Pick" && action.Name is null && action.Entity == read.Entity))
            {
                diagnostics.Report("BEV3025", DiagnosticSeverity.Error, $"SearchBy under 'Action Read {read.Entity}' is unused because 'Action Pick {read.Entity}' decides picking; move SearchBy there.", read.SourceSpan!);
            }
        }
    }

    // Read limited to owner fields, while Create lets the creator set those fields to anyone:
    // the creator may not see what they just created.
    private void WarnWhenCreatorsMayNotSeeTheirRecords(IReadOnlyList<ActionModel> actions)
    {
        foreach (var create in actions.Where(action => action.Verb == "Create"))
        {
            var read = actions.FirstOrDefault(action => action.Verb == "Read" && action.Entity == create.Entity);
            if (read is null || read.Authorizations.Any(rule => rule.Kind is "Anyone" or "Authenticated"))
            {
                continue;
            }

            // Members of a role that may read see every record, so creating through that role is fine.
            var owners = read.Authorizations.Where(rule => rule.Kind == "Path").Select(rule => rule.Path!.Segments[0]).ToList();
            var readerRoles = read.Authorizations.Where(rule => rule.Kind == "Role").Select(rule => rule.Role).ToList();
            var creatorsSee = create.Authorizations.Count > 0 &&
                create.Authorizations.All(rule => (rule.Kind == "Path" && owners.Contains(rule.Path!.Segments[0])) || (rule.Kind == "Role" && readerRoles.Contains(rule.Role)));
            if (owners.Count == 0 || creatorsSee)
            {
                continue;
            }

            var ownerPaths = string.Join(" or ", owners.Select(owner => $"'{owner}'"));
            diagnostics.Report("BEV3028", DiagnosticSeverity.Warning, $"Creators may not see what they create: 'Read {create.Entity}' only shows records by {ownerPaths}, but 'Create {create.Entity}' lets them set {ownerPaths} to someone else. Use 'Action Create {create.Entity} By {owners[0]}' to only allow creating records you own.", create.SourceSpan!);
        }
    }

    // A relationship in a generated Create/Update form must resolve to a picker, or no one could fill it in.
    private void ValidatePickableForms(IReadOnlyList<EntityModel> entities, IReadOnlyList<ActionModel> actions)
    {
        foreach (var entity in entities)
        {
            var projected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (verb, experience) in new[] { ("Create", entity.Presentation?.Creating), ("Update", entity.Presentation?.Editing) })
            {
                if (experience?.Mode == "None" || !actions.Any(action => action.Entity == entity.Name && action.Verb == verb))
                {
                    continue;
                }

                projected.UnionWith(experience?.Fields?.Select(path => path.Segments[0]) ?? entity.Fields.Select(field => field.Name));
            }

            foreach (var field in entity.Fields.Where(field => projected.Contains(field.Name)))
            {
                if (field.Type is not { Kind: "Relationship", TargetKind: "Application", Targets.Count: 1, Picker: null } type ||
                    !entities.Any(candidate => candidate.Name == type.Targets[0]) ||
                    actions.Any(action => action.Entity == type.Targets[0] && (action.Verb == "Read" || action is { Verb: "Pick", Name: null })))
                {
                    continue;
                }

                diagnostics.Report("BEV3026", DiagnosticSeverity.Error, $"'{entity.Name}.{field.Name}' targets '{type.Targets[0]}', which declares no Pick or Read, so no one can select it. Add 'Action Pick {type.Targets[0]}' or remove '{field.Name}' from Creating and Editing.", field.SourceSpan!);
            }
        }
    }

    private void ValidateTargetLabels(IReadOnlyList<EntityModel> entities)
    {
        var targets = entities
            .SelectMany(entity => entity.Fields)
            .Where(field => field.Type is { Kind: "Relationship", TargetKind: "Application", Targets.Count: 1 })
            .Select(field => field.Type!.Targets[0])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entity in entities.Where(entity => targets.Contains(entity.Name) && entity.Presentation?.RecordLabel is null && DefaultRecordLabel(entity) is null))
        {
            diagnostics.Report("BEV3027", DiagnosticSeverity.Error, $"'{entity.Name}' has no field that can label its records, so it cannot be a relationship target. Add a Text field or 'Present RecordLabel'.", entity.SourceSpan!);
        }
    }

    // Omitted RecordLabel: the first Text field, otherwise the first other non-Boolean label-eligible field.
    internal static FieldModel? DefaultRecordLabel(EntityModel entity) =>
        entity.Fields.FirstOrDefault(field => field.Type is { Kind: "Primitive", Name: "Text" }) ??
        entity.Fields.FirstOrDefault(field => field.Type?.Kind == "Enum" ||
            (field.Type?.Kind == "Primitive" && field.Type.Name is "Email" or "Phone" or "Url" or "Int" or "Decimal" or "Date" or "DateTime"));

    private static bool IsIdentifier(string text) =>
        text.Length > 0 && char.IsAsciiLetter(text[0]) && text.All(char.IsAsciiLetterOrDigit);

    private IEnumerable<string> ExpandActionVerbs(string verbText, SourceSpan span)
    {
        foreach (var verb in verbText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (verb == "Crud")
            {
                yield return "Create";
                yield return "Read";
                yield return "Update";
                yield return "Delete";
                continue;
            }

            if (verb is "Create" or "Read" or "Update" or "Delete" or "Pick")
            {
                yield return verb;
                continue;
            }

            diagnostics.Report("BEV2003", DiagnosticSeverity.Error, $"Unsupported action verb '{verb}'.", span);
        }
    }
}
