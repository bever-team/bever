using Bever.Model;

namespace Bever.Core;

// Compiles 'Rule <condition> "<message>"' against the fields of its entity. Every problem is BEV6001
// with a message that names it; the first problem in a rule stops that rule.
internal sealed class RuleSemanticBuilder
{
    private static readonly string[] Operators = ["!=", "<=", ">=", "=", "<", ">"];

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "And", "Or", "Not", "In", "Null", "Today", "Now", "True", "False"
    };

    private readonly EntityModel entity;
    private readonly DiagnosticBag diagnostics;
    private readonly SourceSpan span;
    private readonly List<string> tokens;
    private int position;

    private RuleSemanticBuilder(EntityModel entity, DiagnosticBag diagnostics, SourceSpan span, List<string> tokens)
    {
        this.entity = entity;
        this.diagnostics = diagnostics;
        this.span = span;
        this.tokens = tokens;
    }

    public static RuleModel? Build(RuleDeclarationSyntax syntax, EntityModel entity, DiagnosticBag diagnostics)
    {
        var text = syntax.Text;
        var messageStart = text.Length > 1 && text[^1] == '"' ? text.LastIndexOf('"', text.Length - 2) : -1;
        var message = messageStart < 0 ? string.Empty : text[(messageStart + 1)..^1];
        if (messageStart < 0 || message.Trim().Length == 0 || text[..messageStart].Trim().Length == 0)
        {
            diagnostics.Report("BEV6001", DiagnosticSeverity.Error, "A rule needs a condition and a quoted message, for example 'Rule Amount > 0 \"Amount must be positive\"'.", syntax.Span);
            return null;
        }

        try
        {
            var builder = new RuleSemanticBuilder(entity, diagnostics, syntax.Span, Tokenize(text[..messageStart]));
            var condition = builder.ParseOr();
            if (builder.position < builder.tokens.Count)
            {
                throw new RuleError($"Unexpected '{builder.tokens[builder.position]}' in rule. Combine conditions with And or Or.");
            }

            return new RuleModel(condition, message, syntax.Span);
        }
        catch (RuleError error)
        {
            if (error.Message.Length > 0)
            {
                diagnostics.Report("BEV6001", DiagnosticSeverity.Error, error.Message, syntax.Span);
            }

            return null;
        }
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            var start = index;
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '"')
            {
                index = text.IndexOf('"', index + 1);
                if (index < 0)
                {
                    throw new RuleError("A quoted value in the rule is not closed.");
                }

                tokens.Add(text[start..++index]);
                continue;
            }

            if (current is '(' or ')' or ',')
            {
                tokens.Add(current.ToString());
                index++;
                continue;
            }

            var op = Operators.FirstOrDefault(candidate => string.CompareOrdinal(text, index, candidate, 0, candidate.Length) == 0);
            if (op is not null)
            {
                tokens.Add(op);
                index += op.Length;
                continue;
            }

            if (char.IsAsciiDigit(current) || (current == '-' && index + 1 < text.Length && char.IsAsciiDigit(text[index + 1])))
            {
                index++;
                while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] == '.'))
                {
                    index++;
                }
            }
            else if (char.IsAsciiLetter(current) || current == '/')
            {
                // '/' is kept in the token so paths get the usual field path diagnostics.
                while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '/'))
                {
                    index++;
                }
            }
            else
            {
                throw new RuleError($"Unexpected '{current}' in rule.");
            }

            tokens.Add(text[start..index]);
        }

        return tokens;
    }

    private string? Peek => position < tokens.Count ? tokens[position] : null;

    private string Next() => position < tokens.Count
        ? tokens[position++]
        : throw new RuleError("The rule condition is incomplete.");

    private ExpressionModel ParseOr() => ParseChain("Or", ParseAnd);

    private ExpressionModel ParseAnd() => ParseChain("And", ParseNot);

    private ExpressionModel ParseChain(string keyword, Func<ExpressionModel> operand)
    {
        var operands = new List<ExpressionModel> { operand() };
        while (Peek == keyword)
        {
            position++;
            operands.Add(operand());
        }

        return operands.Count == 1 ? operands[0] : new ExpressionModel(keyword, operands: operands);
    }

    private ExpressionModel ParseNot()
    {
        if (Peek == "Not")
        {
            position++;
            return new ExpressionModel("Not", operands: [ParseNot()]);
        }

        if (Peek == "(")
        {
            position++;
            var inner = ParseOr();
            if (Next() != ")")
            {
                throw new RuleError("Missing ')' in rule.");
            }

            return inner;
        }

        return ParseComparison();
    }

    private ExpressionModel ParseComparison()
    {
        var left = Next();
        if (Peek == "In")
        {
            position++;
            return ParseIn(left);
        }

        var op = Next();
        if (!Operators.Contains(op))
        {
            throw new RuleError($"Expected a comparison such as '=', '!=', '<', or '>' after '{left}', not '{op}'.");
        }

        var right = Next();
        var leftField = FieldOf(left);
        var rightField = FieldOf(right);

        // An enum option wins over a field of the same name: 'Status = Open' compares with the option.
        if (leftField?.Type?.Kind == "Enum" && IsOption(leftField, right))
        {
            rightField = null;
        }

        if (rightField?.Type?.Kind == "Enum" && IsOption(rightField, left))
        {
            leftField = null;
        }

        var field = leftField ?? rightField ?? throw new RuleError(IsName(left) || IsName(right)
            ? $"'{(IsName(left) ? left : right)}' is not a field of '{entity.Name}'."
            : $"'{left} {op} {right}' compares no field. Compare a field of '{entity.Name}', for example 'Amount > 0'.");
        if (leftField is not null && rightField is not null)
        {
            CheckFields(leftField, rightField, op);
        }

        var operands = new[]
        {
            leftField is null ? Value(left, field, op) : FieldExpression(leftField, op),
            rightField is null ? Value(right, field, op) : FieldExpression(rightField, op)
        };
        return new ExpressionModel("Compare", op, operands);
    }

    private ExpressionModel ParseIn(string left)
    {
        var field = FieldOf(left) ?? throw new RuleError($"'In' needs a field of '{entity.Name}' on its left, not '{left}'.");
        if (Next() != "(")
        {
            throw new RuleError($"'In' needs a list of values, for example '{left} In (A, B)'.");
        }

        var operands = new List<ExpressionModel> { FieldExpression(field, "=") };
        do
        {
            var value = Next();
            if (value is "Null" or "Today" or "Now" || (FieldOf(value) is not null && !IsOption(field, value)))
            {
                throw new RuleError($"'In' lists values of '{field.Name}', not '{value}'.");
            }

            operands.Add(Value(value, field, "="));
        }
        while (Next() == ",");

        if (tokens[position - 1] != ")")
        {
            throw new RuleError("Separate the values of 'In' with commas and close the list with ')'.");
        }

        return new ExpressionModel("In", operands: operands);
    }

    // The field a token names, or null when it is a keyword or a literal.
    private FieldModel? FieldOf(string token)
    {
        if (!IsName(token) && token[0] != '/')
        {
            return null;
        }

        // An empty RuleError stops the rule without a second diagnostic.
        if (SemanticModelBuilder.ParseFieldName(token, diagnostics, span) is null)
        {
            throw new RuleError(string.Empty);
        }

        var field = entity.Fields.FirstOrDefault(candidate => candidate.Name == token);
        return field is { Type: null } ? throw new RuleError(string.Empty) : field;
    }

    private static bool IsName(string token) => char.IsAsciiLetter(token[0]) && !Keywords.Contains(token);

    private static bool IsOption(FieldModel field, string token) => field.Type!.Options.Any(option => option.Name == token);

    private ExpressionModel FieldExpression(FieldModel field, string op)
    {
        var kind = Category(field.Type!);
        if (kind == "Relationship" && op is not ("=" or "!="))
        {
            throw new RuleError($"Relationship field '{field.Name}' can only be compared with Null.");
        }

        if (op is "<" or "<=" or ">" or ">=" && kind is not ("Number" or "Date" or "DateTime" or "Time"))
        {
            throw new RuleError($"'{op}' compares numbers, dates, and times; '{field.Name}' can only be compared with '=', '!=', or 'In'.");
        }

        return new ExpressionModel("Field", path: new ReferencePathModel([field.Name]));
    }

    // The non-field side of a comparison with 'field'.
    private ExpressionModel Value(string token, FieldModel field, string op)
    {
        var kind = Category(field.Type!);
        switch (token)
        {
            case "Null":
                return op is "=" or "!="
                    ? new ExpressionModel("Null")
                    : throw new RuleError($"Compare with Null using '=' or '!=', for example '{field.Name} != Null'.");
            case "Today" when kind == "Date":
                return new ExpressionModel("Today");
            case "Now" when kind == "DateTime":
                return new ExpressionModel("Now");
            case "Today" or "Now":
                throw new RuleError($"'{token}' compares with {(token == "Today" ? "Date" : "DateTime")} fields, and '{field.Name}' is not one.");
        }

        if (kind == "Relationship")
        {
            throw new RuleError($"Relationship field '{field.Name}' can only be compared with Null.");
        }

        if (IsName(token) && field.Type!.Kind != "Enum")
        {
            throw new RuleError($"'{token}' is not a field of '{entity.Name}'.");
        }

        var literal = SemanticModelBuilder.ParseLiteral(token, field.Type!)
            ?? throw new RuleError($"'{token}' is not a valid value for '{field.Name}'.{(field.Type!.Kind == "Enum" ? $" Use one of {string.Join(", ", field.Type.Options.Select(option => option.Name))}." : string.Empty)}");
        return new ExpressionModel("Literal", literal: literal);
    }

    private static void CheckFields(FieldModel left, FieldModel right, string op)
    {
        var kind = Category(left.Type!);
        var sameEnum = left.Type!.Options.Select(option => option.Name).SequenceEqual(right.Type!.Options.Select(option => option.Name));
        if (kind != Category(right.Type) || kind == "Relationship" || (kind == "Enum" && !sameEnum))
        {
            throw new RuleError($"'{left.Name} {op} {right.Name}' compares fields of different types.");
        }
    }

    // Types that compare with each other.
    private static string Category(FieldTypeModel type) => (type.Kind, type.Name) switch
    {
        ("Primitive", "Int" or "Decimal" or "Money") => "Number",
        ("Primitive", "Text" or "Email" or "Phone" or "Url") => "Text",
        ("Primitive", { } name) => name,
        (var kind, _) => kind
    };

    private sealed class RuleError(string message) : Exception(message);
}
