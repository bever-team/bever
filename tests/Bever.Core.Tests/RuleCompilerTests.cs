using Bever.Core;
using Bever.Model;
using Xunit;

namespace Bever.Core.Tests;

public sealed class RuleCompilerTests
{
    private const string Refund = """
        Entity Refund
            Field Amount Money
            Field Draft Text?
            Field Reason Text?
            Field Minimum Int?
            Field Maximum Int?
            Field Due Date?
            Field ShippedOn DateTime?
            Field Opens Time?
            Field Express Boolean
            Field Status Enum(Draft, Approved, Rejected)
            Field Owner -> Identity?

        Identity

        """;

    [Fact]
    public void CompilesRulesIntoStructuredConditions()
    {
        var result = Compile("""
                Rule Amount > -0.5 "Amount must be positive"
                Rule Status In (Draft, Approved) Or (Reason != Null And Not Express = True) "Give a reason"
                Rule Minimum <= Maximum And Due >= Today And ShippedOn < Now "Ranges # are checked"
            """);

        Assert.True(result.Success, Diagnostics(result));
        var rules = result.Model.Entities[0].Rules;
        Assert.Equal(["Amount must be positive", "Give a reason", "Ranges # are checked"], rules.Select(rule => rule.Message));

        var amount = rules[0].Condition!;
        Assert.Equal(("Compare", ">"), (amount.Kind, amount.Operator));
        Assert.Equal(["Amount"], amount.Operands[0].Path!.Segments);
        Assert.Equal(("Decimal", -0.5m), (amount.Operands[1].Literal!.Kind, amount.Operands[1].Literal!.Value));

        var reason = rules[1].Condition!;
        Assert.Equal("Or", reason.Kind);
        Assert.Equal(["Field", "Literal", "Literal"], reason.Operands[0].Operands.Select(operand => operand.Kind));
        Assert.Equal(("Identifier", "Approved"), (reason.Operands[0].Operands[2].Literal!.Kind, reason.Operands[0].Operands[2].Literal!.Value));
        Assert.Equal(["Compare", "Not"], reason.Operands[1].Operands.Select(operand => operand.Kind));
        Assert.Equal("Null", reason.Operands[1].Operands[0].Operands[1].Kind);

        var ranges = rules[2].Condition!;
        Assert.Equal(["Field", "Field"], ranges.Operands[0].Operands.Select(operand => operand.Kind));
        Assert.Equal("Today", ranges.Operands[1].Operands[1].Kind);
        Assert.Equal("Now", ranges.Operands[2].Operands[1].Kind);
    }

    [Fact]
    public void EnumOptionsWinOverFieldsOfTheSameNameAndFieldsMayStandOnEitherSide()
    {
        var result = Compile("""
                Rule Draft = Status Or 0 < Amount "Ok"
            """);

        Assert.True(result.Success, Diagnostics(result));
        var condition = result.Model.Entities[0].Rules[0].Condition!;
        Assert.Equal(["Literal", "Field"], condition.Operands[0].Operands.Select(operand => operand.Kind));
        Assert.Equal(["Literal", "Field"], condition.Operands[1].Operands.Select(operand => operand.Kind));
    }

    [Theory]
    [InlineData("Rule Amount > 0", "A rule needs a condition and a quoted message")]
    [InlineData("Rule \"Message\"", "A rule needs a condition and a quoted message")]
    [InlineData("Rule Amount > 0 \"\"", "A rule needs a condition and a quoted message")]
    [InlineData("Rule Amout > 0 \"m\"", "'Amout' is not a field of 'Refund'.")]
    [InlineData("Rule 1 = 1 \"m\"", "'1 = 1' compares no field.")]
    [InlineData("Rule Amount > \"ten\" \"m\"", "'\"ten\"' is not a valid value for 'Amount'.")]
    [InlineData("Rule Status = Open \"m\"", "'Open' is not a valid value for 'Status'. Use one of Draft, Approved, Rejected.")]
    [InlineData("Rule Reason > \"a\" \"m\"", "'>' compares numbers, dates, and times")]
    [InlineData("Rule Status < Approved \"m\"", "'<' compares numbers, dates, and times")]
    [InlineData("Rule Amount > Null \"m\"", "Compare with Null using '=' or '!='")]
    [InlineData("Rule Due > Now \"m\"", "'Now' compares with DateTime fields")]
    [InlineData("Rule Amount = Today \"m\"", "'Today' compares with Date fields")]
    [InlineData("Rule Owner = Reason \"m\"", "'Owner = Reason' compares fields of different types.")]
    [InlineData("Rule Amount = Reason \"m\"", "'Amount = Reason' compares fields of different types.")]
    [InlineData("Rule Owner = \"x\" \"m\"", "Relationship field 'Owner' can only be compared with Null.")]
    [InlineData("Rule Amount > 0 Reason != Null \"m\"", "Unexpected 'Reason' in rule.")]
    [InlineData("Rule (Amount > 0 \"m\"", "The rule condition is incomplete.")]
    [InlineData("Rule Amount > \"m", "A rule needs a condition and a quoted message")]
    [InlineData("Rule Amount ~ 0 \"m\"", "Unexpected '~' in rule.")]
    [InlineData("Rule Status In Draft \"m\"", "'In' needs a list of values")]
    [InlineData("Rule Status In (Draft Approved) \"m\"", "Separate the values of 'In' with commas")]
    [InlineData("Rule Status In (Draft, Null) \"m\"", "'In' lists values of 'Status', not 'Null'.")]
    [InlineData("Rule Amount In (Maximum) \"m\"", "'In' lists values of 'Amount', not 'Maximum'.")]
    [InlineData("Rule Amount > Maximum Or Express \"m\"", "The rule condition is incomplete.")]
    [InlineData("Rule Express Is True \"m\"", "Expected a comparison such as '=', '!=', '<', or '>' after 'Express', not 'Is'.")]
    public void ReportsInvalidRules(string rule, string message)
    {
        var result = Compile($"    {rule}\n");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("BEV6001", diagnostic.Code);
        Assert.StartsWith(message, diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Rule /Amount > 0 \"m\"", "BEV3010")]
    [InlineData("Rule Owner/Email != Null \"m\"", "BEV3031")]
    public void ReportsFieldPathsWithTheUsualDiagnostics(string rule, string code)
    {
        var result = Compile($"    {rule}\n");

        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void RulesOnActionsAreReserved()
    {
        var result = Compile("Action Update Refund\n    By Anyone\n    Rule Amount > 0 \"m\"\n".Insert(0, "\n"));

        Assert.Contains(result.Diagnostics, item => item.Code == "BEV0009" && item.Message.StartsWith("'Rule' is not supported yet.", StringComparison.Ordinal));
    }

    // The rules are indented members of Refund, which the Identity declaration would otherwise close.
    private static CompilationResult Compile(string members) =>
        new BeverCompiler().Compile([new SourceText("inline.bev", Refund.Replace("\nIdentity\n", "\n" + members + "\nIdentity\n"))]);

    private static string Diagnostics(CompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.ToString()));
}
