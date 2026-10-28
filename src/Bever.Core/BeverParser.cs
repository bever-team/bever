using Bever.Model;

namespace Bever.Core;

public sealed class BeverParser
{
    private readonly DiagnosticBag diagnostics;

    public BeverParser(DiagnosticBag diagnostics)
    {
        this.diagnostics = diagnostics;
    }

    // Planned language features that are reserved but not implemented yet. Rule is implemented on entities only.
    private static readonly HashSet<string> PlannedKeywords = new(StringComparer.Ordinal)
    {
        "Aggregate", "Approval", "Audit", "Computed", "Notify", "On", "Query", "Rule", "Schedule", "Seed", "State", "Transition", "When"
    };

    public CompilationUnitSyntax Parse(SourceText source)
    {
        var members = new List<MemberSyntax>();
        EntityBuilder? currentEntity = null;
        ActionBuilder? currentAction = null;
        int? skipDeeperThan = null;
        var insideUnknownTopLevelBlock = false;
        var insideRole = false;

        for (var lineIndex = 0; lineIndex < source.Lines.Count; lineIndex++)
        {
            var line = source.Lines[lineIndex];
            var content = StripComment(line.Text);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var indentation = CountIndentation(content);
            var trimmed = content.Trim();
            var span = CreateSpan(source.Path, line, content);

            if (content.Contains('\t'))
            {
                diagnostics.Report("BEV0012", DiagnosticSeverity.Error, "Use spaces, not tabs, for indentation.", span);
            }

            if (skipDeeperThan is not null)
            {
                if (indentation > skipDeeperThan)
                {
                    continue;
                }

                skipDeeperThan = null;
            }

            if (indentation == 0)
            {
                FlushCurrentBlock();
                insideUnknownTopLevelBlock = false;
                insideRole = false;

                if (trimmed == "Import" || trimmed.StartsWith("Import ", StringComparison.Ordinal))
                {
                    members.Add(ParseImport(trimmed, span));
                    continue;
                }

                var exported = false;
                if (trimmed == "Export" || trimmed.StartsWith("Export ", StringComparison.Ordinal))
                {
                    var exportedText = trimmed["Export".Length..].Trim();
                    if (exportedText != "Entity" && !exportedText.StartsWith("Entity ", StringComparison.Ordinal))
                    {
                        diagnostics.Report("BEV0014", DiagnosticSeverity.Error, "Only entities can be exported: 'Export Entity <Name>'.", span);
                        members.Add(new UnknownTopLevelMemberSyntax(trimmed, span));
                        insideUnknownTopLevelBlock = true;
                        continue;
                    }

                    trimmed = exportedText;
                    exported = true;
                }

                if (trimmed == "Entity" || trimmed.StartsWith("Entity ", StringComparison.Ordinal))
                {
                    var name = trimmed["Entity".Length..].Trim();
                    if (name.Length == 0)
                    {
                        diagnostics.Report("BEV0001", DiagnosticSeverity.Error, "Entity name is required.", span);
                        insideUnknownTopLevelBlock = true;
                        continue;
                    }

                    currentEntity = new EntityBuilder(name, span, exported);
                    continue;
                }

                if (trimmed == "Identity")
                {
                    members.Add(ParseIdentity(source, ref lineIndex, indentation, span));
                    continue;
                }

                if (trimmed.StartsWith("Identity ", StringComparison.Ordinal))
                {
                    diagnostics.Report("BEV5006", DiagnosticSeverity.Error, "Identity declaration does not accept inline arguments. Use top-level 'Identity' with indented members.", span);
                    members.Add(new UnknownTopLevelMemberSyntax(trimmed, span));
                    insideUnknownTopLevelBlock = true;
                    continue;
                }

                if (trimmed.StartsWith("Enable ", StringComparison.Ordinal))
                {
                    diagnostics.Report("BEV5005", DiagnosticSeverity.Error, "Generic Enable syntax is not supported. Use top-level 'Identity' to enable Bever identity.", span);
                    members.Add(new UnknownTopLevelMemberSyntax(trimmed, span));
                    insideUnknownTopLevelBlock = true;
                    continue;
                }

                if (trimmed == "Role" || trimmed.StartsWith("Role ", StringComparison.Ordinal))
                {
                    members.Add(new RoleDeclarationSyntax(trimmed["Role".Length..].Trim(), span));
                    insideRole = true;
                    continue;
                }

                if (trimmed == "Action" || trimmed.StartsWith("Action ", StringComparison.Ordinal))
                {
                    currentAction = new ActionBuilder(trimmed["Action".Length..].Trim(), span);
                    continue;
                }

                diagnostics.Report("BEV0003", DiagnosticSeverity.Error, Unsupported(trimmed, "declaration", "Import, Entity, Identity, Role, or Action"), span);
                members.Add(new UnknownTopLevelMemberSyntax(trimmed, span));
                insideUnknownTopLevelBlock = true;
                continue;
            }

            // Levels and inheritance from the language drafts are planned; a role has no members today.
            if (insideRole)
            {
                diagnostics.Report("BEV5010", DiagnosticSeverity.Error, $"Roles have no members: '{trimmed.Split(' ', 2)[0]}' is not supported yet. See https://bever.dev/roadmap for planned language features.", span);
                skipDeeperThan = indentation;
                continue;
            }

            if (currentEntity is null)
            {
                if (currentAction is not null)
                {
                    var actionMemberIndentation = currentAction.MemberIndentation ??= indentation;
                    if (indentation > actionMemberIndentation)
                    {
                        diagnostics.Report("BEV0009", DiagnosticSeverity.Error, $"Unexpected nested action member: {trimmed}", span);
                        skipDeeperThan = actionMemberIndentation;
                        continue;
                    }

                    var actionMember = ParseActionMember(trimmed, span);
                    currentAction.Members.Add(actionMember);
                    if (actionMember is UnknownActionMemberSyntax)
                    {
                        skipDeeperThan = indentation;
                    }

                    continue;
                }

                if (insideUnknownTopLevelBlock)
                {
                    continue;
                }

                diagnostics.Report("BEV0002", DiagnosticSeverity.Error, "Indented member is not inside a declaration.", span);
                continue;
            }

            var entityMemberIndentation = currentEntity.MemberIndentation ?? indentation;
            currentEntity.MemberIndentation = entityMemberIndentation;
            if (indentation > entityMemberIndentation)
            {
                diagnostics.Report("BEV0004", DiagnosticSeverity.Error, $"Unexpected nested entity member: {trimmed}", span);
                currentEntity.Members.Add(new UnknownEntityMemberSyntax(trimmed, span));
                skipDeeperThan = entityMemberIndentation;
                continue;
            }

            if (indentation < entityMemberIndentation)
            {
                currentEntity.MemberIndentation = indentation;
            }

            if (trimmed == "Present")
            {
                currentEntity.Members.Add(ParsePresent(source, ref lineIndex, indentation, span));
                continue;
            }

            if (trimmed.StartsWith("Present ", StringComparison.Ordinal))
            {
                diagnostics.Report("BEV0010", DiagnosticSeverity.Error, "Present does not accept arguments.", span);
                currentEntity.Members.Add(new UnknownEntityMemberSyntax(trimmed, span));
                skipDeeperThan = indentation;
                continue;
            }

            var entityMember = ParseEntityMember(trimmed, span);
            currentEntity.Members.Add(entityMember);
            if (entityMember is UnknownEntityMemberSyntax)
            {
                skipDeeperThan = indentation;
            }
        }

        FlushCurrentBlock();

        var unitSpan = source.Lines.Count == 0
            ? EmptySpan(source.Path)
            : new SourceSpan(
                new SourceLocation(source.Path, 1, 1),
                new SourceLocation(source.Path, source.Lines[^1].LineNumber, source.Lines[^1].Text.Length + 1));

        return new CompilationUnitSyntax(members, unitSpan);

        void FlushEntity()
        {
            if (currentEntity is null)
            {
                return;
            }

            var span = ExtendSpan(currentEntity.Span, currentEntity.Members.LastOrDefault()?.Span);
            members.Add(new EntityDeclarationSyntax(currentEntity.Name, currentEntity.Members.ToList(), span, currentEntity.Exported));
            currentEntity = null;
        }

        void FlushAction()
        {
            if (currentAction is null)
            {
                return;
            }

            var span = ExtendSpan(currentAction.Span, currentAction.Members.LastOrDefault()?.Span);
            members.Add(new ActionDeclarationSyntax(currentAction.Text, currentAction.Members.ToList(), span));
            currentAction = null;
        }

        void FlushCurrentBlock()
        {
            FlushEntity();
            FlushAction();
            skipDeeperThan = null;
        }
    }

    // "'Rule' is not supported yet." for planned keywords, otherwise "Unknown <kind> 'X'. Expected <expected>."
    private static string Unsupported(string text, string kind, string expected)
    {
        var keyword = text.Split(' ', 2)[0];
        return PlannedKeywords.Contains(keyword)
            ? $"'{keyword}' is not supported yet. See https://bever.dev/roadmap for planned language features."
            : $"Unknown {kind} '{keyword}'. Expected {expected}.";
    }

    private PresentDeclarationSyntax ParsePresent(
        SourceText source,
        ref int lineIndex,
        int indentation,
        SourceSpan declarationSpan)
    {
        var cursor = lineIndex + 1;
        var members = ParsePresentationMembers(source, ref cursor, indentation);
        lineIndex = cursor - 1;
        return new PresentDeclarationSyntax(members, ExtendSpan(declarationSpan, members.LastOrDefault()?.Span));
    }

    private IdentityDeclarationSyntax ParseIdentity(
        SourceText source,
        ref int lineIndex,
        int indentation,
        SourceSpan declarationSpan)
    {
        var members = new List<IdentityMemberSyntax>();
        for (var cursor = lineIndex + 1; cursor < source.Lines.Count; cursor++)
        {
            var line = source.Lines[cursor];
            var content = StripComment(line.Text);
            if (string.IsNullOrWhiteSpace(content))
            {
                lineIndex = cursor;
                continue;
            }

            var memberIndentation = CountIndentation(content);
            if (memberIndentation <= indentation)
            {
                break;
            }

            members.Add(new IdentityMemberSyntax(content.Trim(), CreateSpan(source.Path, line, content)));
            lineIndex = cursor;
        }

        return new IdentityDeclarationSyntax(members, ExtendSpan(declarationSpan, members.LastOrDefault()?.Span));
    }

    private IReadOnlyList<PresentationMemberSyntax> ParsePresentationMembers(
        SourceText source,
        ref int cursor,
        int parentIndentation)
    {
        var members = new List<PresentationMemberSyntax>();
        int? levelIndentation = null;

        while (cursor < source.Lines.Count)
        {
            var line = source.Lines[cursor];
            var content = StripComment(line.Text);
            if (string.IsNullOrWhiteSpace(content))
            {
                cursor++;
                continue;
            }

            var indentation = CountIndentation(content);
            if (indentation <= parentIndentation)
            {
                break;
            }

            var currentLevelIndentation = levelIndentation ?? indentation;
            levelIndentation = currentLevelIndentation;
            if (indentation < currentLevelIndentation)
            {
                diagnostics.Report(
                    "BEV0011",
                    DiagnosticSeverity.Error,
                    "Presentation indentation does not match the containing scope.",
                    CreateSpan(source.Path, line, content));
                levelIndentation = indentation;
                currentLevelIndentation = indentation;
            }

            if (indentation > currentLevelIndentation)
            {
                // A deeper line belongs to the previous declaration. If there is no
                // previous declaration, recover by treating it as this level.
                if (members.Count == 0)
                {
                    levelIndentation = indentation;
                }
                else
                {
                    break;
                }
            }

            var span = CreateSpan(source.Path, line, content);
            var text = content.Trim();
            cursor++;
            var children = ParsePresentationMembers(source, ref cursor, indentation);
            members.Add(new PresentationMemberSyntax(text, children, ExtendSpan(span, children.LastOrDefault()?.Span)));
        }

        return members;
    }

    // Import <Name>, ... From "<path>.bev", the path relative to the importing file.
    private MemberSyntax ParseImport(string text, SourceSpan span)
    {
        var fromIndex = text.IndexOf(" From ", StringComparison.Ordinal);
        var names = fromIndex < 0 ? [] : text["Import".Length..fromIndex].Split(',', StringSplitOptions.TrimEntries);
        var path = fromIndex < 0 ? string.Empty : text[(fromIndex + " From ".Length)..].Trim();
        if (names.Length == 0 || names.Any(name => name.Length == 0 || name.Contains(' ')) ||
            path.Length < 2 || path[0] != '"' || !path.EndsWith(".bev\"", StringComparison.Ordinal))
        {
            diagnostics.Report("BEV0013", DiagnosticSeverity.Error, "Import takes entity names and a quoted .bev path, for example 'Import Customer, Product From \"sales.bev\"'.", span);
            return new UnknownTopLevelMemberSyntax(text, span);
        }

        return new ImportDeclarationSyntax(names, path[1..^1], span);
    }

    private ActionMemberSyntax ParseActionMember(string text, SourceSpan span)
    {
        if (text.StartsWith("By ", StringComparison.Ordinal))
        {
            return new AuthorizationClauseSyntax(text["By ".Length..].Trim(), span);
        }

        if (text.StartsWith("OrderBy ", StringComparison.Ordinal))
        {
            return new OrderByClauseSyntax(text["OrderBy ".Length..].Trim(), span);
        }

        if (text == "SearchBy" || text.StartsWith("SearchBy ", StringComparison.Ordinal))
        {
            return new SearchByClauseSyntax(text.Length == "SearchBy".Length ? string.Empty : text["SearchBy ".Length..].Trim(), span);
        }

        diagnostics.Report("BEV0009", DiagnosticSeverity.Error, Unsupported(text, "action member", "By, OrderBy, or SearchBy"), span);
        return new UnknownActionMemberSyntax(text, span);
    }

    private EntityMemberSyntax ParseEntityMember(string text, SourceSpan span)
    {
        if (text == "Field")
        {
            diagnostics.Report("BEV0005", DiagnosticSeverity.Error, "Field name is required.", span);
            return new UnknownEntityMemberSyntax(text, span);
        }

        // The condition is parsed by the semantic builder, which knows the entity's fields.
        if (text == "Rule" || text.StartsWith("Rule ", StringComparison.Ordinal))
        {
            return new RuleDeclarationSyntax(text["Rule".Length..].Trim(), span);
        }

        if (!text.StartsWith("Field ", StringComparison.Ordinal))
        {
            diagnostics.Report("BEV0004", DiagnosticSeverity.Error, Unsupported(text, "entity member", "Field, Rule, or Present"), span);
            return new UnknownEntityMemberSyntax(text, span);
        }

        text = text["Field ".Length..].Trim();
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            diagnostics.Report("BEV0005", DiagnosticSeverity.Error, "Field name is required.", span);
            return new UnknownEntityMemberSyntax(text, span);
        }

        if (parts.Length < 2)
        {
            diagnostics.Report("BEV0006", DiagnosticSeverity.Error, $"Field '{parts[0]}' requires a type.", span);
            return new UnknownEntityMemberSyntax(text, span);
        }

        return new FieldDeclarationSyntax(parts[0], parts[1], span);
    }

    private static string StripComment(string line)
    {
        var inString = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                inString = !inString;
            }

            if (!inString && line[index] == '#')
            {
                return line[..index];
            }
        }

        return line;
    }

    private static int CountIndentation(string line)
    {
        var count = 0;
        while (count < line.Length && char.IsWhiteSpace(line[count]))
        {
            count++;
        }

        return count;
    }

    private static SourceSpan CreateSpan(string path, SourceLine line, string content)
    {
        var indentation = CountIndentation(content);
        return new SourceSpan(
            new SourceLocation(path, line.LineNumber, indentation + 1),
            new SourceLocation(path, line.LineNumber, Math.Max(indentation + 1, content.Length + 1)));
    }

    private static SourceSpan ExtendSpan(SourceSpan start, SourceSpan? end) =>
        end is null ? start : new SourceSpan(start.Start, end.End);

    private static SourceSpan EmptySpan(string path) => new(
        new SourceLocation(path, 1, 1),
        new SourceLocation(path, 1, 1));

    private sealed record EntityBuilder(string Name, SourceSpan Span, bool Exported)
    {
        public List<EntityMemberSyntax> Members { get; } = [];

        public int? MemberIndentation { get; set; }
    }

    private sealed record ActionBuilder(string Text, SourceSpan Span)
    {
        public List<ActionMemberSyntax> Members { get; } = [];

        public int? MemberIndentation { get; set; }
    }
}
