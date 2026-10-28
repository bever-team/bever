using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bever.Model;
using Xunit;

namespace Bever.Core.Tests;

public sealed class SemanticModelContractTests
{
    private const string FixtureDirectory = "fixtures/semantic-model";
    private const string AbsentFixture = "bever-model-0.10-presentation-absent.json";
    private const string EmptyFixture = "bever-model-0.10-empty.json";
    private const string PopulatedFixture = "bever-model-0.10-identity-populated.json";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void CompleteModelWithoutPresentationSerializesExactlyToSharedFixture()
    {
        AssertModelMatchesFixture(CreateModelWithoutPresentation(), AbsentFixture);
    }

    [Fact]
    public void CompleteIdentityModelSerializesExactlyToSharedFixture()
    {
        AssertModelMatchesFixture(CreatePopulatedModel(), PopulatedFixture);
    }

    [Fact]
    public void CreateEmptySerializesExactlyToSharedFixture()
    {
        AssertModelMatchesFixture(BeverModel.CreateEmpty(), EmptyFixture);
    }

    [Fact]
    public void CreateEmptyUsesCurrentSemanticModelVersion()
    {
        Assert.Equal("0.10", BeverModel.CreateEmpty().SemanticModelVersion);
        Assert.False(BeverModel.CreateEmpty().Identity.Enabled);
    }

    [Theory]
    [InlineData(AbsentFixture)]
    [InlineData(EmptyFixture)]
    [InlineData(PopulatedFixture)]
    public void CompleteSharedFixturesDeserializeAndReserializeByteExactly(string fixtureName)
    {
        var fixtureBytes = ReadCanonicalFixture(fixtureName);
        var model = JsonSerializer.Deserialize<BeverModel>(fixtureBytes, JsonOptions);

        Assert.NotNull(model);
        Assert.Equal(fixtureBytes, SerializeCanonicalFixture(model));
    }

    [Fact]
    public void CanonicalDisabledAndEnabledIdentityValuesRemainDistinct()
    {
        var absent = DeserializeFixture(AbsentFixture);
        var populated = DeserializeFixture(PopulatedFixture);
        var task = Assert.Single(populated.Entities, entity => entity.Name == "Task");

        Assert.False(absent.Identity.Enabled);
        Assert.True(populated.Identity.Enabled);
        Assert.Equal(["Login", "Logout", "Session", "Register"], populated.Identity.Capabilities);
        Assert.Equal("Email", populated.Identity.Summary!.LabelSource);
        Assert.Equal("Identity", Assert.Single(populated.Identity.SystemTargets).Name);
        Assert.Equal("Application", task.Kind);
        var assignee = Assert.Single(task.Fields, field => field.Name == "Assignee");
        Assert.True(assignee.Type!.Nullable);
        Assert.Equal(["Identity"], assignee.Type.Targets);
        Assert.Equal("SystemIdentity", assignee.Type.TargetKind);
        Assert.Equal("Manager", Assert.Single(populated.Roles).Name);
        Assert.Empty(absent.Roles);
    }

    [Fact]
    public void AuthorizationsCarryRoleInCanonicalOrder()
    {
        using var document = JsonDocument.Parse(ReadCanonicalFixture(PopulatedFixture));
        var authorizations = document.RootElement.GetProperty("actions")[0].GetProperty("authorizations");

        Assert.All(authorizations.EnumerateArray(), item => Assert.Equal(["kind", "path", "role"], item.EnumerateObject().Select(property => property.Name).ToArray()));
        Assert.Equal(JsonValueKind.Null, authorizations[0].GetProperty("role").ValueKind);
        Assert.Equal("Manager", authorizations[1].GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, authorizations[1].GetProperty("path").ValueKind);
    }

    [Fact]
    public void TopLevelPropertiesUseCanonicalOrder()
    {
        using var document = JsonDocument.Parse(ReadCanonicalFixture(PopulatedFixture));

        Assert.Equal(
            ["beverLanguageVersion", "semanticModelVersion", "identity", "roles", "entities", "actions"],
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void ActionModelContractIncludesRequiredNullableSearchByInCanonicalOrder()
    {
        using var document = JsonDocument.Parse(ReadCanonicalFixture(PopulatedFixture));
        var action = document.RootElement.GetProperty("actions")[0];

        Assert.Equal(
            ["verb", "entity", "name", "sourceSpan", "authorizations", "orderBy", "searchBy"],
            action.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("Create", action.GetProperty("verb").GetString());
        Assert.Equal("Task", action.GetProperty("entity").GetString());
        Assert.Equal(JsonValueKind.Null, action.GetProperty("orderBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, action.GetProperty("searchBy").ValueKind);
    }

    [Fact]
    public void ActionModelSourceConstructorSerializesExplicitSearchByAsStructuredPaths()
    {
        var action = new ActionModel(
            "Read",
            "Customer",
            Span("customer.bev", 1, 1, 3, 1),
            [],
            null,
            [Path("Name")]);

        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(action, JsonOptions));
        var root = document.RootElement;

        Assert.Equal(["verb", "entity", "name", "sourceSpan", "authorizations", "orderBy", "searchBy"], root.EnumerateObject().Select(property => property.Name).ToArray());
        var searchBy = Assert.Single(root.GetProperty("searchBy").EnumerateArray());
        Assert.Equal("Name", Assert.Single(searchBy.GetProperty("segments").EnumerateArray()).GetString());
        Assert.Equal(["segments"], searchBy.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Theory]
    [InlineData(AbsentFixture)]
    [InlineData(EmptyFixture)]
    [InlineData(PopulatedFixture)]
    public void SharedFixturesAreBomlessUtf8WithOneLfTerminator(string fixtureName)
    {
        var bytes = File.ReadAllBytes(GetFixturePath(fixtureName));

        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var text = StrictUtf8.GetString(bytes);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text);
        Assert.False(text.EndsWith("\n\n", StringComparison.Ordinal));
    }

    private static BeverModel CreateModelWithoutPresentation() =>
        new(
            "0.6",
            "0.10",
            [
                new EntityModel(
                    "Task",
                    Span("presentation-absent.bev", 1, 1, 3, 1),
                    [new FieldModel("Title", Primitive("Text", false, 200), null, Span("presentation-absent.bev", 2, 5, 2, 26))])
            ],
            [
                new ActionModel(
                    "Read",
                    "Task",
                    Span("presentation-absent.bev", 4, 1, 6, 1),
                    [new AuthorizationModel("Anyone", null)],
                    null)
            ]);

    private static BeverModel CreatePopulatedModel()
    {
        const string path = "identity-task.bev";

        return new BeverModel(
            "0.6",
            "0.10",
            new IdentityModel(
                true,
                ["Login", "Logout", "Session", "Register"],
                new IdentitySummaryModel("Email"),
                [new IdentitySystemTargetModel("Identity", "SystemIdentity", new IdentityLookupModel("IdLabel", true, false))]),
            [
                new EntityModel(
                    "Task",
                    Span(path, 1, 1, 4, 1),
                    [
                        new FieldModel("Title", Primitive("Text", false, 200), null, Span(path, 2, 5, 2, 26)),
                        new FieldModel(
                            "Assignee",
                            new FieldTypeModel("Relationship", null, true, [], [], ["Identity"], "SetNull", "SystemIdentity"),
                            null,
                            Span(path, 3, 5, 3, 43))
                    ])
                {
                    Rules =
                    [
                        new RuleModel(
                            new ExpressionModel("Or", operands:
                            [
                                new ExpressionModel("Compare", "!=", [new ExpressionModel("Field", path: Path("Title")), new ExpressionModel("Literal", literal: new LiteralValueModel("Text", "Untitled"))]),
                                new ExpressionModel("Compare", "!=", [new ExpressionModel("Field", path: Path("Assignee")), new ExpressionModel("Null")])
                            ]),
                            "Name or assign the task",
                            Span(path, 4, 5, 4, 72))
                    ]
                }
            ],
            [
                new ActionModel("Create", "Task", Span(path, 5, 1, 7, 1), [new AuthorizationModel("Path", Path("Assignee")), new AuthorizationModel("Role", null, "Manager")], null)
            ],
            [new RoleModel("Manager", Span(path, 8, 1, 8, 13))]);
    }

    private static FieldTypeModel Primitive(string name, bool nullable, int? maxLength = null) =>
        new(
            "Primitive",
            name,
            nullable,
            maxLength is null ? [] : [new Dictionary<string, object?> { ["maxLength"] = maxLength.Value }],
            [],
            [],
            null);

    private static void AssertModelMatchesFixture(BeverModel model, string fixtureName) =>
        Assert.Equal(ReadCanonicalFixture(fixtureName), SerializeCanonicalFixture(model));

    private static BeverModel DeserializeFixture(string fixtureName) =>
        JsonSerializer.Deserialize<BeverModel>(ReadCanonicalFixture(fixtureName), JsonOptions)!;

    private static byte[] ReadCanonicalFixture(string fixtureName)
    {
        var bytes = File.ReadAllBytes(GetFixturePath(fixtureName));
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        _ = StrictUtf8.GetString(bytes);
        return bytes;
    }

    private static byte[] SerializeCanonicalFixture(BeverModel model)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(model, JsonOptions);
        var canonical = new byte[serialized.Length + 1];
        serialized.CopyTo(canonical, 0);
        canonical[^1] = (byte)'\n';
        return canonical;
    }

    private static string GetFixturePath(string fixtureName, [CallerFilePath] string sourcePath = "") =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath)!, FixtureDirectory, fixtureName);

    private static string FieldName(ReferencePathModel path) => Assert.Single(path.Segments);

    private static ReferencePathModel Path(string segment) => new([segment]);

    private static SourceSpan Span(string path, int startLine, int startColumn, int endLine, int endColumn) =>
        new(new SourceLocation(path, startLine, startColumn), new SourceLocation(path, endLine, endColumn));
}
