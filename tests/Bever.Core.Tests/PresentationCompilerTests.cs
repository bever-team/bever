using System.Text.Json;
using Bever.Core;
using Bever.Model;
using Xunit;

namespace Bever.Core.Tests;

public sealed class PresentationCompilerTests
{
    [Fact]
    public void CompilesCompletePresentationVocabularyWithCanonicalReferencesAndSpans()
    {
        var result = Compile("complete.bev", """
        Entity Task
            Field Title Text(200)
            Field Description Text(5000)
            Field Assignee -> User?
            Field Status Enum(Pending, Completed)

            Present
                Label "Task #1" # outside comment
                PluralLabel "Tasks"
                RecordLabel Title
                Field Title
                    Label "Task title"
                Listing
                    Fields Title, Assignee, Status
                Details
                    Fields Title, Description, Assignee, Status
                Creating
                    Input Assignee
                        Picker Modal
                    Fields Title, Description, Assignee, Status
                    Mode Modal
                Editing
                    Fields Title, Description, Assignee
                    Mode Inline
                Actions
                    Create
                        Emphasis Primary
                        Place ListingToolbar
                        Order -2147483648
                    Read
                        Emphasis Default
                        Place Navigation, ListingRow, DetailsToolbar
                    uPdAtE
                        Label "Edit # task"
                        Confirm "Save # changes?"
                        Place ListingRow, DetailsToolbar
                        Order 2147483647
                        Hidden false
                    Delete
                        Emphasis Destructive
                        Hidden true

        Entity User
            Field Name Text(100)

        Action Crud Task
            By Authenticated

        Action Read User
            By Anyone
        """);

        Assert.True(result.Success, Diagnostics(result));
        Assert.Equal("0.6", result.Model.BeverLanguageVersion);
        Assert.Equal("0.10", result.Model.SemanticModelVersion);
        var task = Assert.Single(result.Model.Entities, entity => entity.Name == "Task");
        var presentation = Assert.IsType<EntityPresentationModel>(task.Presentation);
        Assert.Equal("Task #1", presentation.Label);
        Assert.Equal(["Title", "Assignee", "Status"], presentation.Listing!.Fields!.Select(FieldName));
        Assert.Equal(["Title", "Description", "Assignee", "Status"], presentation.Creating!.Fields!.Select(FieldName));
        Assert.Equal("Modal", presentation.Creating.Mode);
        Assert.Equal("ReferencePicker", Assert.Single(presentation.Creating.Inputs).Widget);
        Assert.Equal("Modal", Assert.Single(presentation.Creating.Inputs).Display);
        Assert.Equal("Inline", presentation.Editing!.Mode);
        Assert.Equal("Update", Assert.Single(presentation.ActionOverrides, item => item.Action!.Name == "Update").Action!.Name);
        Assert.Equal("Edit # task", Assert.Single(presentation.ActionOverrides, item => item.Action!.Name == "Update").Label);
        Assert.Equal(int.MinValue, Assert.Single(presentation.ActionOverrides, item => item.Action!.Name == "Create").Order);
        Assert.Equal(int.MaxValue, Assert.Single(presentation.ActionOverrides, item => item.Action!.Name == "Update").Order);
        Assert.Equal("complete.bev", presentation.SourceSpan!.Start.Path);
        Assert.Equal(7, presentation.SourceSpan.Start.Line);
        Assert.True(presentation.SourceSpan.End.Line > presentation.SourceSpan.Start.Line);
        Assert.Equal(11, Assert.Single(presentation.Fields).SourceSpan!.Start.Line);

        var json = JsonSerializer.Serialize(result.Model, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("\"semanticModelVersion\":\"0.10\"", json);
        Assert.Contains("\"action\":{\"name\":\"Update\"}", json);
        Assert.DoesNotContain("uPdAtE", json);
    }

    [Fact]
    public void ResolvesFieldsActionsAndPickerTargetsAcrossFilesRegardlessOfSourceOrder()
    {
        var result = new BeverCompiler().Compile(
        [
            new SourceText("actions.bev", """
            Import Task From "task.bev"
            Import User From "user.bev"

            Action Update, Create Task
                By Authenticated
            Action Read User
                By Anyone
            """),
            new SourceText("task.bev", """
            Import User From "user.bev"

            Export Entity Task
                Present
                    Creating
                        Mode Page
                        Input Owner
                            Picker Modal
                    Editing
                        Mode Modal
                    Actions
                        update
                            Hidden false
                Field Owner -> User
                Field Title Text(100)
            """),
            new SourceText("user.bev", """
            Export Entity User
                Field Name Text(100)
            """)
        ]);

        Assert.True(result.Success, Diagnostics(result));
        var presentation = Assert.Single(result.Model.Entities, entity => entity.Name == "Task").Presentation!;
        Assert.Equal("Update", Assert.Single(presentation.ActionOverrides).Action!.Name);
        Assert.Equal("Owner", FieldName(Assert.Single(presentation.Creating!.Inputs).Field!));
    }

    [Fact]
    public void MutationOnlyEditingAndOverridesDoNotRequireRead()
    {
        var result = Compile("mutation-only.bev", """
        Entity ModerationItem
            Field Note Text(1000)
            Present
                Editing
                    Mode Inline
                Actions
                    Update
                        Place ListingRow, DetailsToolbar
                    Delete
                        Hidden false

        Action Update, Delete ModerationItem
            By Authenticated
        """);

        Assert.True(result.Success, Diagnostics(result));
        Assert.DoesNotContain(result.Model.Actions, action => action.Verb == "Read");
        Assert.Equal("Inline", Assert.Single(result.Model.Entities).Presentation!.Editing!.Mode);
    }

    [Theory]
    [InlineData("Dropdown")]
    [InlineData("Combobox")]
    [InlineData("Modal")]
    public void AcceptsApprovedPickerDisplaysAsPresentationOnlyMetadata(string display)
    {
        var result = Compile("picker.bev", $$"""
        Entity Task
            Field Owner -> User
            Present
                Creating
                    Mode Page
                    Input Owner
                        Picker {{display}}

        Entity User
            Field Name Text(100)

        Action Create Task
            By Authenticated
        Action Read User
            By Anyone
        """);

        Assert.True(result.Success, Diagnostics(result));
        var input = Assert.Single(Assert.Single(result.Model.Entities, entity => entity.Name == "Task").Presentation!.Creating!.Inputs);
        Assert.Equal("ReferencePicker", input.Widget);
        Assert.Equal(display, input.Display);
        Assert.DoesNotContain(result.Model.Actions, action => action.Verb == display);
    }

    [Fact]
    public void AllowsIdentityPickerWithoutReadIdentity()
    {
        var result = Compile("identity-picker.bev", """
        Identity

        Entity Task
            Field Owner -> Identity
            Present
                Creating
                    Mode Page
                    Input Owner
                        Picker Combobox

        Action Create Task
            By Authenticated
        """);

        Assert.True(result.Success, Diagnostics(result));
        var input = Assert.Single(Assert.Single(result.Model.Entities).Presentation!.Creating!.Inputs);
        Assert.Equal("ReferencePicker", input.Widget);
        Assert.Equal("SystemIdentity", Assert.Single(result.Model.Entities).Fields[0].Type!.TargetKind);
        Assert.DoesNotContain(result.Model.Actions, action => action.Entity == "Identity");
    }

    [Fact]
    public void NoneExperiencesAndBareActionsAreValidWithoutActionsOrContext()
    {
        var result = Compile("dormant.bev", """
        Entity Draft
            Field Name Text(100)
            Present
                Creating
                    Mode None
                Editing
                    Mode None
                Actions
        """);

        Assert.True(result.Success, Diagnostics(result));
        Assert.Empty(Assert.Single(result.Model.Entities).Presentation!.ActionOverrides);
    }

    [Fact]
    public void BareActionOverrideIsAValidNoOp()
    {
        var result = Compile("bare-override.bev", """
        Entity Task
            Field Name Text(100)
            Present
                Actions
                    Update

        Action Update Task
            By Authenticated
        """);

        Assert.True(result.Success, Diagnostics(result));
        var item = Assert.Single(Assert.Single(result.Model.Entities).Presentation!.ActionOverrides);
        Assert.Equal("Update", item.Action!.Name);
        Assert.Null(item.Label);
        Assert.Null(item.Placements);
        Assert.Null(item.Hidden);
    }

    [Theory]
    [InlineData("Text(10)")]
    [InlineData("Email")]
    [InlineData("Phone")]
    [InlineData("Url")]
    [InlineData("Int")]
    [InlineData("Decimal")]
    [InlineData("Enum(One, Two)")]
    [InlineData("Date")]
    [InlineData("DateTime")]
    [InlineData("Boolean")]
    public void AcceptsEveryApprovedRecordLabelType(string type)
    {
        var result = Compile("record-label.bev", $"""
        Entity Item
            Field Value {type}
            Present
                RecordLabel Value
        """);

        Assert.True(result.Success, Diagnostics(result));
    }

    [Theory]
    [MemberData(nameof(InvalidPresentSources))]
    public void RejectsInvalidPresentationWithSourceLocatedDiagnostic(string name, string source, int errorLine, string messagePart)
    {
        var result = Compile($"{name}.bev", source);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, item =>
            item.Severity == DiagnosticSeverity.Error && item.Message.Contains(messagePart, StringComparison.Ordinal));
        Assert.Equal($"{name}.bev", diagnostic.Span.Start.Path);
        Assert.Equal(errorLine, diagnostic.Span.Start.Line);
        Assert.True(diagnostic.Span.Start.Column > 0);
        Assert.True(diagnostic.Span.End.Column >= diagnostic.Span.Start.Column);
    }

    [Fact]
    public void ReportsBadIndentationAndRecoversToValidateFollowingPresentationMembers()
    {
        var result = Compile("indentation.bev", """
        Entity Task
            Field Title Text(100)
            Present
                    Label "Task"
              SearchBy Title
                PluralLabel "Tasks"
        """);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "BEV0011" && item.Span.Start.Line == 5);
        Assert.Contains(result.Diagnostics, item => item.Code == "BEV4001" && item.Span.Start.Line == 5 && item.Message.Contains("SearchBy"));
    }

    [Fact]
    public void NestedEntityDescendantsAreNotReinterpretedAsFieldsOrPresentBlocks()
    {
        var result = Compile("entity-scope.bev", """
        Entity Task
            State Status
                Field Accidental Text(100)
                Present
                    Label "Accidental"
            Field Title Text(100)
        """);

        // The unsupported State block is one error; its nested lines are skipped, not reparsed as fields or Present.
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(("BEV0004", 2), (diagnostic.Code, diagnostic.Span.Start.Line));
    }

    [Theory]
    [InlineData("Action Crud Task\n    By Anyone", "Page")]
    [InlineData("Action Read Task\n    By Anyone", "None")]
    public void OmittedModeKeepsTheDefaultExperience(string actions, string mode)
    {
        var result = Compile("omitted-mode.bev", Source("Editing\n    Fields Title", actions: actions));

        Assert.True(result.Success, Diagnostics(result));
        Assert.Equal(mode, result.Model.Entities[0].Presentation!.Editing!.Mode);
    }

    public static IEnumerable<object[]> InvalidPresentSources()
    {
        yield return Invalid("duplicate-present", """
            Entity Task
                Field Title Text(100)
                Present
                Present
            """, 4, "Duplicate Present");
        yield return Invalid("unknown-member", Source("SearchBy Title"), 4, "SearchBy");
        yield return Invalid("executable-member", Source("Set /Title = \"x\""), 4, "Set");
        yield return Invalid("duplicate-label", Source("Label \"One\"\nLabel \"Two\""), 5, "Duplicate presentation declaration");
        yield return Invalid("unquoted-label", Source("Label Task"), 4, "quoted text");
        yield return Invalid("duplicate-surface", Source("Listing\n    Fields Title\nListing"), 6, "Duplicate");
        yield return Invalid("duplicate-surface-fields", Source("Listing\n    Fields Title\n    Fields Title"), 6, "Duplicate presentation declaration");
        yield return Invalid("duplicate-actions", Source("Actions\nActions"), 5, "Duplicate presentation declaration");
        yield return Invalid("leading-slash", Source("RecordLabel /Title"), 4, "no leading '/'");
        yield return Invalid("traversal", Source("RecordLabel Title/Value"), 4, "not supported yet");
        yield return Invalid("unknown-field", Source("RecordLabel Missing"), 4, "Unknown field");
        yield return Invalid("record-label-relationship", """
            Entity User
                Field Name Text(100)
            Entity Task
                Field Owner -> User
                Present
                    RecordLabel Owner
            """, 6, "allowed display type");
        yield return Invalid("empty-fields", Source("Listing\n            Fields"), 5, "at least one");
        yield return Invalid("duplicate-projection", Source("Listing\n            Fields Title, Title"), 5, "Duplicate field path");
        yield return Invalid("input-allowlist", Source("Creating\n            Mode None\n            Fields Title\n            Input Other\n                Picker Modal", "Field Other -> User"), 8, "not one of the Creating fields");
        yield return Invalid("picker-primitive", Source("Creating\n            Mode None\n            Input Title\n                Picker Modal"), 6, "single-target relationship");
        yield return Invalid("picker-polymorphic", Source("Creating\n            Mode None\n            Input Owner\n                Picker Modal", "Field Owner -> User Or Admin"), 7, "single-target relationship");
        yield return Invalid("picker-target-read", Source("Creating\n            Mode None\n            Input Owner\n                Picker Modal", "Field Owner -> User"), 7, "must declare Pick or Read");
        yield return Invalid("picker-display", Source("Creating\n            Mode None\n            Input Title\n                Picker Drawer"), 7, "Picker must be Dropdown, Combobox, or Modal");
        yield return Invalid("missing-picker", Source("Creating\n            Mode None\n            Input Title"), 6, "requires exactly one Picker");
        yield return Invalid("undeclared-hidden-action", Source("Actions\n            Delete\n                Hidden true"), 5, "undeclared action");
        yield return Invalid("undeclared-custom-action", Source("Actions\n            MarkCompleted"), 5, "undeclared action");
        yield return Invalid("pick-override", Source("Actions\n            Pick", actions: "Action Pick Task\n    By Anyone"), 5, "Pick has no generated control");
        yield return Invalid("empty-place", Source("Actions\n            Update\n                Place", actions: "Action Update Task\n    By Anyone"), 6, "at least one");
        yield return Invalid("duplicate-place", Source("Actions\n            Update\n                Place ListingRow, ListingRow", actions: "Action Update Task\n    By Anyone"), 6, "Duplicate placement");
        yield return Invalid("unknown-place", Source("Actions\n            Update\n                Place Dashboard", actions: "Action Update Task\n    By Anyone"), 6, "Unknown placement");
        yield return Invalid("wrong-place-scope", Source("Actions\n            Update\n                Place ListingToolbar", actions: "Action Update Task\n    By Anyone"), 6, "not valid for action");
        yield return Invalid("fractional-order", Source("Actions\n            Update\n                Order 1.5", actions: "Action Update Task\n    By Anyone"), 6, "whole number");
        yield return Invalid("overflow-order", Source("Actions\n            Update\n                Order 2147483648", actions: "Action Update Task\n    By Anyone"), 6, "whole number");
        yield return Invalid("invalid-hidden", Source("Actions\n            Update\n                Hidden True", actions: "Action Update Task\n    By Anyone"), 6, "lowercase true or false");
        yield return Invalid("invalid-emphasis", Source("Actions\n            Update\n                Emphasis Loud", actions: "Action Update Task\n    By Anyone"), 6, "Unknown Emphasis");
        yield return Invalid("creating-inline", Source("Creating\n            Mode Inline"), 5, "Unknown Creating mode");
        yield return Invalid("editing-mode", Source("Editing\n            Mode Drawer"), 5, "Unknown Editing mode");
        yield return Invalid("creating-without-create", Source("Creating\n            Mode Modal"), 4, "requires action 'Create'");
        yield return Invalid("editing-without-update", Source("Editing\n            Mode Inline"), 4, "requires action 'Update'");
        yield return Invalid("duplicate-override", Source("Actions\n            Update\n            update", actions: "Action Update Task\n    By Anyone"), 6, "Duplicate action override");
        yield return Invalid("duplicate-property", Source("Actions\n            Update\n                Hidden true\n                Hidden false", actions: "Action Update Task\n    By Anyone"), 7, "Duplicate presentation declaration");
        yield return Invalid("duplicate-field-override", Source("Field Title\n    Label \"One\"\nField Title\n    Label \"Two\""), 6, "Duplicate presentation Field");
        yield return Invalid("missing-field-label", Source("Field Title"), 4, "requires exactly one Label");
        yield return Invalid("duplicate-input", Source("Creating\n            Mode None\n            Input Owner\n                Picker Modal\n            Input Owner\n                Picker Modal", "Field Owner -> User", "Action Read User\n    By Anyone"), 9, "Duplicate Input");
    }

    private static object[] Invalid(string name, string source, int line, string message) => [name, source, line, message];

    private static string Source(string presentMembers, string? extraField = null, string? actions = null) =>
        $"Entity Task\n    Field Title Text(100)\n{(extraField is null ? string.Empty : $"    {extraField}\n")}    Present\n        {presentMembers.Replace("\n", "\n        ")}\n{actions ?? string.Empty}";

    private static CompilationResult Compile(string path, string source) =>
        new BeverCompiler().Compile([new SourceText(path, source)]);

    private static string FieldName(ReferencePathModel path) => Assert.Single(path.Segments);

    private static string Diagnostics(CompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.ToString()));
}
