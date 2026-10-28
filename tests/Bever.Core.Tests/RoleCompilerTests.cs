using Bever.Core;
using Bever.Model;
using Xunit;

namespace Bever.Core.Tests;

public sealed class RoleCompilerTests
{
    [Fact]
    public void CompilesRolesAndRoleRules()
    {
        var result = Compile(
            ("roles.bev", """
            Identity

            Role Agent
            Role Manager
            """),
            ("tickets.bev", """
            Entity Ticket
                Field Subject Text(200)
                Field Owner -> Identity

            Action Create, Read, Update Ticket
                By Agent
                By Owner

            Action Delete Ticket
                By Manager
            """));

        Assert.True(result.Success, string.Join('\n', result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(["Agent", "Manager"], result.Model.Roles.Select(role => role.Name));
        Assert.Equal(("roles.bev", 3), (result.Model.Roles[0].SourceSpan!.Start.Path, result.Model.Roles[0].SourceSpan!.Start.Line));
        var read = result.Model.Actions.Single(action => action.Verb == "Read");
        Assert.Collection(
            read.Authorizations,
            rule => Assert.Equal(("Role", null, "Agent"), (rule.Kind, rule.Path, rule.Role)),
            rule => Assert.Equal(("Path", "Owner", null), (rule.Kind, rule.Path!.Segments[0], rule.Role)));
    }

    [Theory]
    [InlineData("Role Agent\nRole agent", "BEV5008", "Duplicate role 'agent'.")]
    [InlineData("Role", "BEV5009", "Role needs a name, for example 'Role Agent'.")]
    [InlineData("Role Field Agent", "BEV5009", "Role name 'Field Agent' must start with a letter and contain only letters and digits.")]
    [InlineData("Role Administrator", "BEV5009", "'Administrator' is reserved and cannot be a role name.")]
    [InlineData("Role Authenticated", "BEV5009", "'Authenticated' is reserved and cannot be a role name.")]
    [InlineData("Role Agent\n    Levels Junior < Senior", "BEV5010", "Roles have no members: 'Levels' is not supported yet.")]
    [InlineData("Role Manager\n    Inherits Agent", "BEV5010", "Roles have no members: 'Inherits' is not supported yet.")]
    public void RejectsInvalidRoleDeclarations(string declaration, string code, string message)
    {
        var result = Compile(("app.bev", $"Identity\n\n{declaration}\n"));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.StartsWith(message, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RolesRequireIdentity()
    {
        var result = Compile(("app.bev", "Role Agent\n"));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(("BEV5007", 1, "Role 'Agent' requires a top-level Identity declaration."), (diagnostic.Code, diagnostic.Span.Start.Line, diagnostic.Message));
    }

    [Theory]
    [InlineData("By agent", "BEV3021", "'By agent' names no role or field.")]
    [InlineData("By Reviewer", "BEV3036", "'By Reviewer' is ambiguous: 'Reviewer' is both a field of 'Ticket' and a role. Rename one of them.")]
    public void RoleRulesNameDeclaredRolesExactly(string authorization, string code, string message)
    {
        var result = Compile(("app.bev", $"""
            Identity

            Role Agent
            Role Reviewer

            Entity Ticket
                Field Subject Text(200)
                Field Reviewer -> Identity

            Action Read Ticket
                {authorization}
            """));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.StartsWith(message, diagnostic.Message, StringComparison.Ordinal);
    }

    private static CompilationResult Compile(params (string Path, string Text)[] files) =>
        new BeverCompiler().Compile(files.Select(file => new SourceText(file.Path, file.Text)).ToList());
}
