using Bever.Core;
using Bever.Model;
using Xunit;

namespace Bever.Core.Tests;

public sealed class BeverCompilerTests
{
    [Fact]
    public void ParsesExplicitFieldDeclarations()
    {
        var source = """
        Entity Products
            Field Name Text(100)
            Field BuyPrice Money
            Field Website Url?
        """;

        var result = Compile(source);

        Assert.True(result.Success, Diagnostics(result));
        Assert.Single(result.Model.Entities);
        Assert.Equal("Products", result.Model.Entities[0].Name);
        Assert.Equal(["Name", "BuyPrice", "Website"], result.Model.Entities[0].Fields.Select(field => field.Name));

        var nameType = result.Model.Entities[0].Fields[0].Type;
        Assert.NotNull(nameType);
        Assert.Equal("Primitive", nameType.Kind);
        Assert.Equal("Text", nameType.Name);
        Assert.Equal(100, nameType.Arguments[0]["maxLength"]);

        var buyPriceType = result.Model.Entities[0].Fields[1].Type;
        Assert.NotNull(buyPriceType);
        Assert.Equal("Primitive", buyPriceType.Kind);
        Assert.Equal("Money", buyPriceType.Name);
        Assert.False(buyPriceType.Nullable);

        Assert.True(result.Model.Entities[0].Fields[2].Type!.Nullable);
    }

    [Theory]
    [InlineData("Field Images Multiple(10) Image", "BEV3031")]
    [InlineData("Field Photo Image", "BEV3031")]
    [InlineData("Field Contract File", "BEV3031")]
    [InlineData("Field Estimate Duration", "BEV3031")]
    [InlineData("Field Author -> Agent Or Customer", "BEV3031")]
    [InlineData("Field Weight Float", "BEV3032")]
    [InlineData("Field Count Int(5)", "BEV3033")]
    [InlineData("Field Name Text(0)", "BEV3004")]
    [InlineData("Field Name Text(abc)", "BEV3004")]
    [InlineData("Field Level Enum(Low, low)", "BEV3035")]
    [InlineData("Field Level Enum(1st, 2nd)", "BEV3035")]
    [InlineData("Field Id Text(10)", "BEV1011")]
    [InlineData("Field createdOn DateTime", "BEV1011")]
    [InlineData("Field Title Text(10)\n    Field title Text(10)", "BEV1012")]
    [InlineData("Field first-name Text(10)", "BEV1010")]
    public void RejectsUnsupportedOrInvalidFields(string field, string code)
    {
        var result = Compile($"Entity Customer\n    {field.Replace("\\n", "\n")}\n");

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("Field Active Boolean = True", "Boolean", true)]
    [InlineData("Field Count Int = -3", "Integer", -3)]
    [InlineData("Field Level Enum(Low, High) = High", "Identifier", "High")]
    [InlineData("Field Name Text(20) = \"Untitled\"", "Text", "Untitled")]
    [InlineData("Field Starts Time = \"09:30\"", "Text", "09:30")]
    public void CompilesTypedDefaultValues(string field, string kind, object value)
    {
        var result = Compile($"Entity Customer\n    {field}\n");

        Assert.True(result.Success, Diagnostics(result));
        var literal = result.Model.Entities[0].Fields[0].DefaultValue;
        Assert.NotNull(literal);
        Assert.Equal(kind, literal.Kind);
        Assert.Equal(value, literal.Value);
    }

    [Fact]
    public void CompilesDecimalDefaultsAsNumbers()
    {
        var result = Compile("Entity Product\n    Field Price Money = 9.95\n");

        Assert.True(result.Success, Diagnostics(result));
        Assert.Equal(9.95m, result.Model.Entities[0].Fields[0].DefaultValue!.Value);
    }

    [Theory]
    [InlineData("Field Active Boolean = Yes")]
    [InlineData("Field Count Int = 2.5")]
    [InlineData("Field Price Money = cheap")]
    [InlineData("Field Level Enum(Low, High) = Medium")]
    [InlineData("Field Name Text(3) = \"Too long\"")]
    [InlineData("Field Name Text(20) = Untitled")]
    [InlineData("Field Contact Email = \"not-an-email\"")]
    [InlineData("Field Due Date = \"31/01/2026\"")]
    [InlineData("Field Owner -> Customer = \"x\"")]
    public void RejectsDefaultValuesThatDoNotMatchTheFieldType(string field)
    {
        var result = Compile($"Entity Customer\n    {field}\n");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "BEV3034");
    }    [Fact]
    public void RejectsShorthandAndPlannedEntityMembersOnce()
    {
        var source = """
        Entity Task
            Field Title Text(1000)
            Description Text(10000)
            State Status
                Backlog
                Done
        """;

        var result = Compile(source);

        Assert.False(result.Success);
        Assert.Collection(
            result.Diagnostics,
            item => Assert.Equal(("BEV0004", 3, "Unknown entity member 'Description'. Expected Field, Rule, or Present."), (item.Code, item.Span.Start.Line, item.Message)),
            item => Assert.Equal(("BEV0004", 4), (item.Code, item.Span.Start.Line)));
        Assert.StartsWith("'State' is not supported yet.", result.Diagnostics[1].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("On ArrivalOf Task/Reminder\n    Notify /CreatedBy", "BEV0003", "'On' is not supported yet.")]
    [InlineData("Entities Task", "BEV0003", "Unknown declaration 'Entities'. Expected Import, Entity, Identity, Role, or Action.")]
    [InlineData("Action Update Task\n    By Anyone\n    Rule /Title != Null \"Title required\"", "BEV0009", "'Rule' is not supported yet.")]
    [InlineData("Action Create Task\n    By Anyone\n    OnCreate\n        Set /Title = \"x\"", "BEV0009", "Unknown action member 'OnCreate'. Expected By, OrderBy, or SearchBy.")]
    public void RejectsUnsupportedDeclarationsWithOneErrorPerBlock(string declaration, string code, string message)
    {
        var result = Compile($"Entity Task\n    Field Title Text(100)\n\n{declaration}\n");

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.StartsWith(message, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresEveryActionToSayWhoMayPerformIt()
    {
        var result = Compile("""
        Entity Task
            Field Title Text(100)

        Action Read Task
            OrderBy Title
        """);

        Assert.False(result.Success);
        Assert.Equal("BEV2007", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void RejectsTabIndentation()
    {
        var result = Compile("Entity Task\n\tField Title Text(100)\n");

        Assert.Contains(result.Diagnostics, item => item.Code == "BEV0012");
    }

    [Fact]
    public void RejectsEntitiesThatShareARoute()
    {
        var result = Compile("""
        Entity Box
            Field Name Text(100)

        Entity Boxe
            Field Name Text(100)
        """);

        Assert.Equal("BEV1005", Assert.Single(result.Diagnostics).Code);
    }    [Fact]
    public void ReportsFieldWithoutTypeAsDiagnostic()
    {
        var source = """
        Entity Product
            Field Name
        """;

        var result = Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "BEV0006");
        Assert.Empty(result.Model.Entities);
    }

    [Fact]
    public void FailedCompilesReturnFreshEmptyModels()
    {
        var first = Compile("""
        Entity Product
            Field Name
        """);
        first.Model.Entities.Add(new EntityModel { Name = "Injected" });

        var second = Compile("""
        Entity Product
            Field Name
        """);

        Assert.False(second.Success);
        Assert.Empty(second.Model.Entities);
    }

    [Fact]
    public void ParsesActionDeclarations()
    {
        var source = """
        Entity Product
            Field Name Text(100)

        Action Read Product
            By Anyone
        """;

        var result = Compile(source);

        Assert.True(result.Success);
        var action = Assert.Single(result.Model.Actions);
        Assert.Equal("Read", action.Verb);
        Assert.Equal("Product", action.Entity);
        var authorization = Assert.Single(action.Authorizations);
        Assert.Equal("Anyone", authorization.Kind);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "BEV0003");
    }

    [Fact]
    public void CompilesReadOrderByOnFieldsAndSystemTimestamps()
    {
        var result = Compile("""
        Entity Task
            Field Description Text(10000)
            Field Completed Boolean
            Field Reminder DateTime?

        Action Create, Update, Delete Task
            By Anyone

        Action Read Task
            By Anyone
            OrderBy CreatedOn Desc
        """);

        Assert.True(result.Success, Diagnostics(result));
        var entity = Assert.Single(result.Model.Entities);
        Assert.Equal(["Description", "Completed", "Reminder"], entity.Fields.Select(field => field.Name));
        Assert.Equal(["Create", "Update", "Delete", "Read"], result.Model.Actions.Select(action => action.Verb));

        var readAction = Assert.Single(result.Model.Actions, action => action.Verb == "Read");
        Assert.NotNull(readAction.OrderBy);
        AssertPath(["CreatedOn"], readAction.OrderBy.Path);
        Assert.Equal("Desc", readAction.OrderBy.Direction);
    }

    [Fact]
    public void CrudDeclarationsApplyOrderByAndSearchByToRead()
    {
        var result = Compile("""
        Entity Task
            Field Title Text(100)

        Action Crud Task
            By Anyone
            OrderBy Title Desc
            SearchBy Title
        """);

        Assert.True(result.Success, Diagnostics(result));
        var read = Assert.Single(result.Model.Actions, action => action.Verb == "Read");
        Assert.Equal("Desc", read.OrderBy!.Direction);
        Assert.Single(read.SearchBy!);
        Assert.All(result.Model.Actions.Where(action => action.Verb != "Read"), action => Assert.True(action.OrderBy is null && action.SearchBy is null));
    }

    [Theory]
    [InlineData("Read", "OrderBy Missing", "BEV3007")]
    [InlineData("Read", "OrderBy Owner", "BEV3007")]
    [InlineData("Read", "OrderBy Title Up", "BEV3008")]
    [InlineData("Read", "OrderBy Title\n    OrderBy Title", "BEV3030")]
    [InlineData("Update", "OrderBy Title", "BEV3029")]
    public void ValidatesOrderBy(string verb, string clause, string code)
    {
        var result = Compile($"Entity Task\n    Field Title Text(100)\n    Field Owner -> Person\n\nEntity Person\n    Field Name Text(100)\n\nAction Read Person\n    By Anyone\n\nAction {verb} Task\n    By Anyone\n    {clause}\n");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == code);
    }    [Fact]
    public void ParsesCommaSeparatedActionDeclarations()
    {
        var result = Compile("""
        Entity Product
            Field Name Text(100)

        Action Create, Update Product
            By Authenticated
        """);

        Assert.True(result.Success);
        Assert.Equal(["Create", "Update"], result.Model.Actions.Select(action => action.Verb));
        Assert.All(result.Model.Actions, action => Assert.Equal("Product", action.Entity));
    }

    [Fact]
    public void ExpandsCrudActionDeclarations()
    {
        var result = Compile("""
        Entity Product
            Field Name Text(100)

        Action Crud Product
            By Authenticated
        """);

        Assert.True(result.Success);
        Assert.Equal(["Create", "Read", "Update", "Delete"], result.Model.Actions.Select(action => action.Verb));
    }

    [Fact]
    public void ReportsActionsForUnknownEntitiesAsErrors()
    {
        var result = Compile("""
        Action Read Product
            By Anyone
        """);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "BEV2002");
        Assert.Empty(result.Model.Actions);
    }

    [Fact]
    public void ReportsDuplicateActionsAsErrors()
    {
        var result = Compile("""
        Entity Product
            Field Name Text(100)

        Action Read Product
            By Anyone

        Action Read Product
            By Anyone
        """);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "BEV2004");
        Assert.Empty(result.Model.Actions);
    }

    [Fact]
    public void ReportsDuplicateEntitiesAsDiagnostics()
    {
        var source = """
        Entity Product
            Field Name Text(100)

        Entity Product
            Field Sku Text(100)
        """;

        var result = Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "BEV1001");
    }

    [Fact]
    public void RejectsEntityNamesThatAreNotIdentifiers()
    {
        var result = Compile("""
        Entity _lookup
            Field Name Text(100)
        """);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "BEV1004");
        Assert.Contains("_lookup", diagnostic.Message);
        Assert.Equal(1, diagnostic.Span.Start.Line);
    }

    [Fact]
    public void IncludesSemanticModelVersions()
    {
        var result = Compile("""
        Entity Product
            Field Name Text(100)
        """);

        Assert.Equal("0.6", result.Model.BeverLanguageVersion);
        Assert.Equal("0.10", result.Model.SemanticModelVersion);
        Assert.False(result.Model.Identity.Enabled);
        Assert.Null(result.Model.Identity.Summary);
        Assert.Empty(result.Model.Identity.Capabilities);
        Assert.Empty(result.Model.Actions);
    }

    [Fact]
    public void CompilesIdentityDefaultsAndRegistrationIntoSemanticModel()
    {
        var result = Compile("""
        Identity
            Registration Enabled
            Summary Label Email

        Entity Task
            Field Title Text(100)
            Field Assignee -> Identity? OnDelete SetNull

        Action Create Task
            By Assignee
        """);

        Assert.True(result.Success, Diagnostics(result));
        Assert.Equal("0.10", result.Model.SemanticModelVersion);
        Assert.True(result.Model.Identity.Enabled);
        Assert.Equal(["Login", "Logout", "Session", "Register"], result.Model.Identity.Capabilities);
        Assert.Equal("Email", result.Model.Identity.Summary!.LabelSource);
        var systemTarget = Assert.Single(result.Model.Identity.SystemTargets);
        Assert.Equal("Identity", systemTarget.Name);
        Assert.Equal("SystemIdentity", systemTarget.Kind);
        Assert.True(systemTarget.Lookup!.ContextBound);
        Assert.False(systemTarget.Lookup.GeneralReadAction);

        var entity = Assert.Single(result.Model.Entities);
        Assert.Equal("Application", entity.Kind);
        var relationship = entity.Fields.Single(field => field.Name == "Assignee").Type!;
        Assert.Equal("SystemIdentity", relationship.TargetKind);
        Assert.Equal(["Identity"], relationship.Targets);
        Assert.Equal("Path", Assert.Single(Assert.Single(result.Model.Actions).Authorizations).Kind);
    }

    [Fact]
    public void EmptyIdentityEnablesLoginLogoutSessionWithDefaultEmailSummary()
    {
        var result = Compile("""
        Identity

        Entity Task
            Field Title Text(100)
        """);

        Assert.True(result.Success, Diagnostics(result));
        Assert.True(result.Model.Identity.Enabled);
        Assert.Equal(["Login", "Logout", "Session"], result.Model.Identity.Capabilities);
        Assert.Equal("Email", result.Model.Identity.Summary!.LabelSource);
        Assert.Single(result.Model.Identity.SystemTargets);
    }

    [Theory]
    [InlineData("Identity\n    Registration Enabled\n    Registration Enabled", "BEV5002", 3)]
    [InlineData("Identity\n    Summary Label Email\n    Summary Label Email", "BEV5003", 3)]
    [InlineData("Identity\n    Roles Enabled", "BEV5004", 2)]
    [InlineData("Identity\n\nIdentity", "BEV5001", 3)]
    public void RejectsInvalidIdentityBlocksWithSourceSpans(string source, string code, int line)
    {
        var result = Compile(source);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == code);
        Assert.Equal(line, diagnostic.Span.Start.Line);
    }

    [Theory]
    [InlineData("Enable Identity", "BEV5005")]
    [InlineData("Identity Enabled", "BEV5006")]
    public void RejectsUnapprovedIdentityEnableSyntax(string source, string code)
    {
        var result = Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == code && item.Span.Start.Line == 1);
    }

    [Fact]
    public void SerializesIdentityAsStructuredMetadataOnly()
    {
        var result = Compile("""
        Identity
            Registration Enabled
        """);

        Assert.True(result.Success, Diagnostics(result));
        var json = System.Text.Json.JsonSerializer.Serialize(
            result.Model,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        Assert.Contains("\"capabilities\":[\"Login\",\"Logout\",\"Session\",\"Register\"]", json);
        Assert.Contains("\"summary\":{\"labelSource\":\"Email\"}", json);
        Assert.DoesNotContain("Registration Enabled", json);
        Assert.DoesNotContain("Summary Label Email", json);
    }

    [Fact]
    public void RejectsReservedIdentityEntityAndActionAndUnactivatedRelationshipTarget()
    {
        var entityResult = Compile("""
        Entity Identity
            Field Name Text(100)
        """);
        Assert.False(entityResult.Success);
        Assert.Contains(entityResult.Diagnostics, item => item.Code == "BEV1003" && item.Span.Start.Line == 1);

        var actionResult = Compile("""
        Identity
        Action Read Identity
            By Authenticated
        """);
        Assert.False(actionResult.Success);
        Assert.Contains(actionResult.Diagnostics, item => item.Code == "BEV2005" && item.Span.Start.Line == 2);

        var fieldResult = Compile("""
        Entity Task
            Field Assignee -> Identity
        """);
        Assert.False(fieldResult.Success);
        Assert.Contains(fieldResult.Diagnostics, item => item.Code == "BEV3000" && item.Span.Start.Line == 2);
    }

    [Theory]
    [InlineData("By Admin", "BEV3021")]
    [InlineData("By Admin Where Level >= Senior", "BEV3005")]
    [InlineData("By /Assignee", "BEV3010")]
    [InlineData("By Assignee/Manager", "BEV3031")]
    [InlineData("By Owner", "BEV3020")]
    public void RejectsUnsupportedAuthorizationFormsClearly(string authorization, string code)
    {
        var result = Compile($$"""
        Identity

        Entity User
            Field Name Text(100)

        Entity Task
            Field Assignee -> Identity
            Field Owner -> User

        Action Update Task
            {{authorization}}
        """);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == code && item.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ParsesReadActionSearchByIntoReferencePaths()
    {
        var result = Compile("""
        Entity Customer
            Field Name Text(100)
            Field Email Email
            Field Status Enum(Active, Archived)

        Action Read Customer
            By Anyone
            SearchBy Name, Email, Status
        """);

        Assert.True(result.Success);
        var action = Assert.Single(result.Model.Actions);
        Assert.Collection(
            action.SearchBy!,
            path => AssertPath(["Name"], path),
            path => AssertPath(["Email"], path),
            path => AssertPath(["Status"], path));
    }

    [Fact]
    public void SerializesCompiledSearchByAsStructuredReferencePathArray()
    {
        var result = Compile("""
        Entity Customer
            Field Name Text(100)

        Action Read Customer
            By Anyone
            SearchBy Name
        """);

        Assert.True(result.Success);
        var json = System.Text.Json.JsonSerializer.Serialize(
            result.Model,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        Assert.Contains("\"searchBy\":[{\"segments\":[\"Name\"]}]", json);
        Assert.DoesNotContain("SearchBy Name", json);
    }

    [Fact]
    public void RejectsSearchByOutsideReadActionWithSourceSpan()
    {
        var result = Compile("""
        Entity Customer
            Field Name Text(100)

        Action Update Customer
            By Authenticated
            SearchBy Name
        """);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "BEV3014");
        Assert.Equal(6, diagnostic.Span.Start.Line);
        Assert.Equal(5, diagnostic.Span.Start.Column);
    }

    [Theory]
    [InlineData("SearchBy", "BEV3015")]
    [InlineData("SearchBy /Name", "BEV3010")]
    [InlineData("SearchBy Name/Value", "BEV3031")]
    [InlineData("SearchBy Missing", "BEV3017")]
    [InlineData("SearchBy Name, Name", "BEV3018")]
    [InlineData("SearchBy Age", "BEV3019")]
    [InlineData("SearchBy BornOn", "BEV3019")]
    [InlineData("SearchBy Enabled", "BEV3019")]
    [InlineData("SearchBy Owner", "BEV3019")]
    [InlineData("SearchBy Balance", "BEV3019")]
    public void ValidatesSearchByFields(string searchByLine, string expectedCode)
    {
        var result = Compile($$"""
        Entity Customer
            Field Name Text(100)
            Field Age Int
            Field BornOn DateTime
            Field Enabled Boolean
            Field Owner -> User
            Field Balance Money

        Entity User
            Field Name Text(100)

        Action Read Customer
            By Anyone
            {{searchByLine}}
        """);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
    }

    [Fact]
    public void PreservesHashInsideStringLiteralsWhenStrippingComments()
    {
        var result = Compile("""
        Entity Product
            Field Name Text(100) = "#abc"
        """);

        Assert.True(result.Success);
        var field = result.Model.Entities[0].Fields[0];
        Assert.NotNull(field.Type);
        Assert.Equal("Text", field.Type.Name);
        Assert.NotNull(field.DefaultValue);
        Assert.Equal("Text", field.DefaultValue.Kind);
        Assert.Equal("#abc", field.DefaultValue.Value);
    }

    [Fact]
    public void ParsesEnumAndRelationshipTypesIntoStructuredModels()
    {
        var result = Compile("""
        Entity Project
            Field Priority Enum(Low = 0, Medium = 1, High = 2)
            Field Owner -> User?

        Entity User
            Field Name Text(100)
        """);

        Assert.True(result.Success);
        var priorityType = result.Model.Entities[0].Fields[0].Type;
        Assert.NotNull(priorityType);
        Assert.Equal("Enum", priorityType.Kind);
        Assert.Equal(["Low", "Medium", "High"], priorityType.Options.Select(option => option.Name));
        Assert.Equal([0, 1, 2], priorityType.Options.Select(option => option.Value));

        var ownerType = result.Model.Entities[0].Fields[1].Type;
        Assert.NotNull(ownerType);
        Assert.Equal("Relationship", ownerType.Kind);
        Assert.True(ownerType.Nullable);
        Assert.Equal(["User"], ownerType.Targets);
        Assert.Equal("Restrict", ownerType.OnDelete);
    }

    [Fact]
    public void ParsesRelationshipDeleteBehavior()
    {
        var result = Compile("""
        Entity Category
            Field Parent -> Category? OnDelete SetNull
            Field Owner -> User OnDelete Cascade
            Field Name Text(100)

        Entity User
            Field Name Text(100)
        """);

        Assert.True(result.Success);
        Assert.Equal("SetNull", result.Model.Entities[0].Fields[0].Type?.OnDelete);
        Assert.Equal("Cascade", result.Model.Entities[0].Fields[1].Type?.OnDelete);
    }

    [Fact]
    public void RejectsSetNullForRequiredRelationships()
    {
        var result = Compile("""
        Entity Category
            Field Parent -> Category OnDelete SetNull
        """);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "BEV3013");
    }

    private const string PickerSource = """
        Identity

        Entity Project
            Field Name Text(200)
            Field Manager -> Identity

        Action Read Project
            By Authenticated

        Action Pick Project As Managed
            By Manager
            SearchBy Name

        Entity Customer
            Field Name Text(200)

        Action Pick Customer
            By Authenticated

        Entity Invoice
            Field Paid Boolean
            Field Number Int

        Action Read Invoice
            By Authenticated

        Entity Task
            Field Title Text(200)
            Field Project -> Project Pick Managed OnDelete Cascade
            Field Customer -> Customer? OnDelete SetNull
        """;

    [Fact]
    public void CompilesDefaultAndNamedPickersAndFieldPickClausesInAnyOrder()
    {
        var result = Compile(PickerSource + "\n    Field Invoice -> Invoice?\n\nAction Crud Task\n    By Authenticated");

        Assert.True(result.Success, Diagnostics(result));
        Assert.Equal("0.10", result.Model.SemanticModelVersion);
        var managed = Assert.Single(result.Model.Actions, action => action.Name == "Managed");
        Assert.Equal(("Pick", "Project"), (managed.Verb, managed.Entity));
        AssertPath(["Name"], Assert.Single(managed.SearchBy!));
        var defaultPick = Assert.Single(result.Model.Actions, action => action is { Verb: "Pick", Entity: "Customer" });
        Assert.Null(defaultPick.Name);
        Assert.DoesNotContain(result.Model.Actions, action => action.Verb == "Pick" && action.Entity == "Task");
        var task = result.Model.Entities.Single(entity => entity.Name == "Task");
        Assert.Equal(("Managed", "Cascade"), (task.Fields[1].Type!.Picker, task.Fields[1].Type!.OnDelete));
        Assert.Equal((null, "SetNull"), (task.Fields[2].Type!.Picker, task.Fields[2].Type!.OnDelete));
    }

    [Theory]
    [InlineData("Action Read Project As Managed\n    By Anyone", "BEV2006")]
    [InlineData("Action Pick Project As managed\n    By Anyone", "BEV2004")]
    [InlineData("Action Pick Invoice\n    By Anyone\n    OrderBy Number", "BEV3023")]
    [InlineData("Action Read Customer\n    By Anyone\n    SearchBy Name", "BEV3025")]
    [InlineData("Entity Note\n    Field Project -> Project Pick Missing", "BEV3024")]
    [InlineData("Entity Note\n    Field Owner -> Identity Pick Managed", "BEV3024")]
    [InlineData("Entity Note\n    Field Project -> Project Pick Managed Pick Managed", "BEV3024")]
    [InlineData("Action Pick Identity\n    By Anyone", "BEV2005")]
    [InlineData("Entity Note\n    Field Title Text(10)\n    Field Item -> Item\n\nEntity Item\n    Field Name Text(10)\n\nAction Create Note\n    By Anyone", "BEV3026")]
    [InlineData("Entity Note\n    Field Item -> Item\n\nEntity Item\n    Field Paid Boolean\n\nAction Pick Item\n    By Anyone", "BEV3027")]
    public void RejectsInvalidPickerDeclarations(string extra, string code)
    {
        var result = Compile(PickerSource + "\n\n" + extra);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Theory]
    [InlineData("By Owner", "By Authenticated", true)]
    [InlineData("By Owner", "By Anyone", true)]
    [InlineData("By Owner", "By Reviewer", true)]
    [InlineData("By Owner", "By Owner", false)]
    [InlineData("By Authenticated", "By Authenticated", false)]
    public void WarnsWhenCreatorsMayNotSeeWhatTheyCreate(string read, string create, bool warns)
    {
        var result = Compile($$"""
        Identity

        Entity Secret
            Field Title Text(100)
            Field Owner -> Identity
            Field Reviewer -> Identity?

        Action Read Secret
            {{read}}

        Action Create Secret
            {{create}}
        """);

        Assert.True(result.Success, Diagnostics(result));
        var warning = result.Diagnostics.SingleOrDefault(diagnostic => diagnostic.Code == "BEV3028");
        Assert.Equal(warns, warning is not null);
        Assert.True(warning is null || (warning.Severity == DiagnosticSeverity.Warning && warning.Span.Start.Line == 11));
    }

    [Fact]
    public void FilesSeeOnlyTheEntitiesTheyDeclareOrImport()
    {
        var result = new BeverCompiler().Compile(
        [
            new SourceText("bev/crm/customers.bev", """
            Export Entity Customer
                Field Name Text(100)

            Entity Note
                Field Body Text
            """),
            new SourceText("bev/sales/orders.bev", """
            Import Customer From "../crm/customers.bev"

            Entity Order
                Field Customer -> Customer

            Action Read Customer
                By Anyone

            Action Read Order
                By Anyone
            """)
        ]);

        Assert.True(result.Success, Diagnostics(result));
        Assert.Equal(["Customer"], Assert.Single(result.Model.Entities, entity => entity.Name == "Order").Fields[0].Type!.Targets);
    }

    [Theory]
    [InlineData("Entity Order\n    Field Customer -> Customer", "BEV1023", "add 'Import Customer From \"customers.bev\"'")]
    [InlineData("Action Read Customer\n    By Anyone", "BEV1023", "Export it there")]
    [InlineData("Import Note From \"customers.bev\"", "BEV1021", "Declare it as 'Export Entity Note'")]
    [InlineData("Import Missing From \"customers.bev\"", "BEV1021", "declares no entity 'Missing'")]
    [InlineData("Import Customer From \"other.bev\"", "BEV1020", "'other.bev'")]
    [InlineData("Import Customer From \"../customers.bev\"", "BEV1020", "'../customers.bev'")]
    [InlineData("Import Customer, Customer From \"customers.bev\"", "BEV1022", "already declared or imported")]
    [InlineData("Import Customer From customers.bev", "BEV0013", "quoted .bev path")]
    [InlineData("Import From \"customers.bev\"", "BEV0013", "quoted .bev path")]
    [InlineData("Export Action Read Customer\n    By Anyone", "BEV0014", "Only entities can be exported")]
    public void ReportsImportAndExportProblems(string orders, string code, string messagePart)
    {
        var result = new BeverCompiler().Compile(
        [
            new SourceText("customers.bev", "Export Entity Customer\n    Field Name Text(100)\nEntity Note\n    Field Body Text\n"),
            new SourceText("orders.bev", orders)
        ]);

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == code);
        Assert.Contains(messagePart, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("orders.bev", diagnostic.Span.Start.Path);
    }

    private static CompilationResult Compile(string source) =>
        new BeverCompiler().Compile([new SourceText("inline.bev", source)]);

    private static void AssertPath(string[] segments, ReferencePathModel? path)
    {
        Assert.NotNull(path);
        Assert.Equal(segments, path.Segments);
    }

    private static string Diagnostics(CompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.ToString()));

}
