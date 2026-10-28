using Bever.Model;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bever.AspNetCore.Tests;

public sealed class BeverModelLoaderTests
{
    [Fact]
    public void RejectsModelWithErrorDiagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-model-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "model.json"), """
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": false, "capabilities": [], "summary": null, "systemTargets": [] },
          "roles": [],
          "entities": [],
          "actions": [],
          "diagnostics": [
            { "code": "BEV1001", "severity": "Error", "message": "Duplicate entity." }
          ]
        }
        """);

        var loader = new BeverModelLoader(
            Options.Create(new BeverOptions { ModelPath = "model.json" }),
            new TestHostEnvironment(directory));

        Assert.Throws<InvalidOperationException>(() => loader.Load());
    }

    [Fact]
    public void ReportsModelVersionMismatchesBeforeDeserializing()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load("""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.2",
          "entities": [], "actions": []
        }
        """));

        Assert.Contains("has version '0.2', but this runtime requires '0.10'", exception.Message);
    }

    [Fact]
    public void RequiresTopLevelIdentity()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load("""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "entities": [], "actions": []
        }
        """));

        Assert.Contains("identity", exception.Message);
    }

    [Fact]
    public void RejectsInvalidIdentityRelationshipShape()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load("""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": false, "capabilities": [], "summary": null, "systemTargets": [] },
          "roles": [],
          "entities": [{ "name": "Task", "kind": "Application", "fields": [{ "name": "Owner", "type": { "kind": "Relationship", "targets": ["Identity"], "targetKind": "Application", "picker": null } }] }],
          "actions": []
        }
        """));

        Assert.Contains("Identity relationship", exception.Message);
    }

    [Fact]
    public void RejectsCaseInsensitiveDuplicateJsonPropertiesBeforeDeserialization()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load("""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": false, "capabilities": [], "summary": null, "systemTargets": [] },
          "roles": [],
          "entities": [{ "name": "Task", "Name": "Other", "fields": [] }],
          "actions": []
        }
        """));

        Assert.Contains("Duplicate JSON property 'Name'", exception.Message);
        Assert.Contains("$.entities[0]", exception.Message);
    }

    [Fact]
    public void RejectsActionMissingRequiredNullableSearchByBeforeDeserialization()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load("""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": false, "capabilities": [], "summary": null, "systemTargets": [] },
          "roles": [],
          "entities": [{ "name": "Task", "fields": [] }],
          "actions": [{ "verb": "Read", "entity": "Task" }]
          
        }
        """));

        Assert.Contains("searchBy", exception.Message);
        Assert.Contains("$.actions[0]", exception.Message);
    }

    [Fact]
    public void RejectsUppercaseActionsMissingRequiredNullableSearchByBeforeDeserialization()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load("""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": false, "capabilities": [], "summary": null, "systemTargets": [] },
          "roles": [],
          "entities": [{ "name": "Task", "fields": [] }],
          "Actions": [{ "verb": "Read", "entity": "Task" }]
          
        }
        """));

        Assert.Contains("searchBy", exception.Message);
        Assert.Contains("$.actions[0]", exception.Message);
    }

    [Fact]
    public void AllowsExplicitNullActionSearchBy()
    {
        var model = Load("""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": false, "capabilities": [], "summary": null, "systemTargets": [] },
          "roles": [],
          "entities": [{ "name": "Task", "fields": [] }],
          "actions": [{ "verb": "Read", "entity": "Task", "name": null, "searchBy": null }]
          
        }
        """);

        Assert.Null(Assert.Single(model.Actions).SearchBy);
    }

    [Theory]
    [InlineData("""{ "verb": "Pick", "entity": "Task", "searchBy": null }""", "[]", "'name' at $.actions[0]")]
    [InlineData("""{ "verb": "Pick", "entity": "Task", "name": null, "searchBy": null }""", """[{ "name": "Title", "type": { "kind": "Primitive", "name": "Text" } }]""", "'picker' at $.entities[0].fields[0].type")]
    public void RejectsMissingRequiredNullablePickerProperties(string action, string fields, string expected)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load($$"""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": false, "capabilities": [], "summary": null, "systemTargets": [] },
          "roles": [],
          "entities": [{ "name": "Task", "fields": {{fields}} }],
          "actions": [{{action}}]
          
        }
        """));

        Assert.Contains(expected, exception.Message);
    }

    [Theory]
    [InlineData("""[]""", """[{ "kind": "Role", "path": null, "role": "Agent" }]""", "role authorization must name a declared role")]
    [InlineData("""[{ "name": "Agent", "sourceSpan": null }]""", """[{ "kind": "Role", "path": null, "role": "agent" }]""", "role authorization must name a declared role")]
    [InlineData("""[{ "name": "Agent", "sourceSpan": null }]""", """[{ "kind": "Authenticated", "path": null, "role": "Agent" }]""", "only Role authorization may name a role")]
    [InlineData("""[{ "name": "Agent", "sourceSpan": null }]""", """[{ "kind": "Role", "path": null }]""", "'role' at $.actions[0].authorizations[0]")]
    [InlineData("""[{ "name": "Anyone", "sourceSpan": null }]""", """[]""", "roles[0].name")]
    [InlineData("""[{ "name": "Agent", "sourceSpan": null }, { "name": "AGENT", "sourceSpan": null }]""", """[]""", "duplicate value 'AGENT'")]
    public void RejectsInvalidRoles(string roles, string authorizations, string expected)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load($$"""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": true, "capabilities": ["Login", "Logout", "Session"], "summary": { "labelSource": "Email" }, "systemTargets": [{ "name": "Identity", "kind": "SystemIdentity", "lookup": { "summaryShape": "IdLabel", "contextBound": true, "generalReadAction": false } }] },
          "roles": {{roles}},
          "entities": [{ "name": "Task", "fields": [{ "name": "Title", "type": { "kind": "Primitive", "name": "Text", "picker": null } }] }],
          "actions": [{ "verb": "Read", "entity": "Task", "name": null, "authorizations": {{authorizations}}, "searchBy": null }]
        }
        """));

        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void RejectsRolesWithoutIdentityAndModelsWithoutRoles()
    {
        var withoutIdentity = Assert.Throws<InvalidOperationException>(() => Load("""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": false, "capabilities": [], "summary": null, "systemTargets": [] },
          "roles": [{ "name": "Agent", "sourceSpan": null }],
          "entities": [], "actions": []
        }
        """));
        var withoutRoles = Assert.Throws<InvalidOperationException>(() => Load("""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": false, "capabilities": [], "summary": null, "systemTargets": [] },
          "entities": [], "actions": []
        }
        """));

        Assert.Contains("roles require enabled identity", withoutIdentity.Message);
        Assert.Contains("'roles' at $", withoutRoles.Message);
    }

    [Fact]
    public void RejectsUnsupportedMetadataProperty()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load("""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": false, "capabilities": [], "summary": null, "systemTargets": [] },
          "roles": [],
          "entities": [{ "name": "Task", "fields": [], "presentation": { "unsupported": true } }],
          "actions": []
        }
        """));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("model.json", exception.Message);
    }

    [Fact]
    public void RejectsUnknownPresentationFieldWithModelPathDiagnostic()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Load("""
        {
          "beverLanguageVersion": "0.6",
          "semanticModelVersion": "0.10",
          "identity": { "enabled": false, "capabilities": [], "summary": null, "systemTargets": [] },
          "roles": [],
          "entities": [{
            "name": "Task",
            "fields": [{ "name": "Title", "type": { "kind": "Primitive", "name": "Text", "picker": null } }],
            "presentation": { "listing": { "fields": [{ "segments": ["Missing"] }] } }
          }],
          "actions": []
        }
        """));

        Assert.Contains("model.json:entities[0].presentation.listing.fields[0]", exception.Message);
    }

    [Fact]
    public void InMemoryModelCannotBypassPresentationValidation()
    {
        var model = BeverModel.CreateEmpty();
        model.Entities.Add(new EntityModel
        {
            Name = "Task",
            Fields = [new FieldModel { Name = "Title", Type = new FieldTypeModel { Kind = "Primitive", Name = "Text" } }],
            Presentation = new EntityPresentationModel
            {
                Listing = new PresentationSurfaceModel
                {
                    Fields = [new ReferencePathModel(["Missing"])]
                }
            }
        });

        var exception = Assert.Throws<InvalidOperationException>(() => new BeverRuntime(model));

        Assert.Contains("<in-memory Bever model>:entities[0].presentation.listing.fields[0]", exception.Message);
    }

    [Fact]
    public void AcceptsCanonicalPresentationReferences()
    {
        var model = CreateValidPresentationModel();

        var runtime = new BeverRuntime(model);

        Assert.Same(model, runtime.Model);
    }

    [Fact]
    public void RejectsInvalidExternalPresentationMetadata()
    {
        Action<BeverModel>[] invalidMutations =
        [
            model => model.Entities[0].Presentation!.RecordLabel = new ReferencePathModel(["Assignee"]),
            model => model.Entities[0].Presentation!.Listing!.Fields = [],
            model => model.Entities[0].Presentation!.Creating!.Mode = "Inline",
            model => model.Entities[0].Presentation!.Creating!.Fields = [new ReferencePathModel(["Title"])],
            model => model.Entities[0].Presentation!.Creating!.Inputs[0].Widget = "Unknown",
            model => model.Entities[0].Presentation!.ActionOverrides[0].Emphasis = "Loud",
            model => model.Entities[0].Presentation!.ActionOverrides[0].Placements = ["Navigation"],
            model => model.Entities[0].Presentation!.ActionOverrides[0].Placements = [],
            model => model.Entities.Add(new EntityModel { Name = "task" }),
            model => model.Entities[0].Fields.Add(new FieldModel { Name = "title" }),
            model => model.Actions.Add(new ActionModel { Entity = "Task", Verb = "Update" })
        ];

        foreach (var mutate in invalidMutations)
        {
            var model = CreateValidPresentationModel();
            mutate(model);

            Assert.Throws<InvalidOperationException>(() => new BeverRuntime(model));
        }
    }

    [Fact]
    public void RejectsPresentationExperienceWithoutRequiredActionOrPickerTargetRead()
    {
        var missingCreate = CreateValidPresentationModel();
        missingCreate.Actions.RemoveAll(action => action.Entity == "Task" && action.Verb == "Create");
        Assert.Throws<InvalidOperationException>(() => new BeverRuntime(missingCreate));

        var missingTargetRead = CreateValidPresentationModel();
        missingTargetRead.Actions.RemoveAll(action => action.Entity == "User" && action.Verb == "Read");
        Assert.Throws<InvalidOperationException>(() => new BeverRuntime(missingTargetRead));
    }

    [Fact]
    public void AllowsContextBoundIdentityPickerWithoutReadIdentityAction()
    {
        var model = BeverModel.CreateEmpty();
        model.Identity = new IdentityModel(true, ["Login", "Logout", "Session"], new IdentitySummaryModel("Email"), [new IdentitySystemTargetModel("Identity", "SystemIdentity", new IdentityLookupModel("IdLabel", true, false))]);
        model.Entities.Add(new EntityModel
        {
            Name = "Task",
            Fields =
            [
                new FieldModel { Name = "Title", Type = new FieldTypeModel { Kind = "Primitive", Name = "Text" } },
                new FieldModel { Name = "Owner", Type = new FieldTypeModel { Kind = "Relationship", Targets = ["Identity"], TargetKind = "SystemIdentity" } }
            ],
            Presentation = new EntityPresentationModel
            {
                Creating = new InputExperiencePresentationModel
                {
                    Mode = "Page",
                    Fields = [new ReferencePathModel(["Title"]), new ReferencePathModel(["Owner"])],
                    Inputs = [new InputPresentationModel { Field = new ReferencePathModel(["Owner"]), Widget = "ReferencePicker", Display = "Combobox" }]
                }
            }
        });
        model.Actions.Add(new ActionModel { Entity = "Task", Verb = "Create" });

        var runtime = new BeverRuntime(model);

        Assert.Same(model, runtime.Model);
    }

    private static BeverModel CreateValidPresentationModel()
    {
        var model = BeverModel.CreateEmpty();
        model.Entities.Add(new EntityModel
        {
            Name = "Task",
            Fields =
            [
                new FieldModel { Name = "Title", Type = new FieldTypeModel { Kind = "Primitive", Name = "Text" } },
                new FieldModel
                {
                    Name = "Assignee",
                    Type = new FieldTypeModel { Kind = "Relationship", Targets = ["User"], TargetKind = "Application" }
                }
            ],
            Presentation = new EntityPresentationModel
            {
                RecordLabel = new ReferencePathModel(["Title"]),
                Listing = new PresentationSurfaceModel { Fields = [new ReferencePathModel(["Title"])] },
                Creating = new InputExperiencePresentationModel
                {
                    Mode = "Page",
                    Fields =
                    [
                        new ReferencePathModel(["Title"]),
                        new ReferencePathModel(["Assignee"])
                    ],
                    Inputs =
                    [
                        new InputPresentationModel
                        {
                            Field = new ReferencePathModel(["Assignee"]),
                            Widget = "ReferencePicker",
                            Display = "Modal"
                        }
                    ]
                },
                Editing = new InputExperiencePresentationModel { Mode = "Inline" },
                ActionOverrides =
                [
                    new ActionPresentationOverrideModel
                    {
                        Action = new ActionReferenceModel("Update"),
                        Emphasis = "Primary",
                        Placements = ["ListingRow"],
                        Order = -10
                    }
                ]
            }
        });
        model.Entities.Add(new EntityModel
        {
            Name = "User",
            Fields = [new FieldModel { Name = "Name", Type = new FieldTypeModel { Kind = "Primitive", Name = "Text" } }]
        });
        model.Actions.AddRange(
        [
            new ActionModel { Entity = "Task", Verb = "Create" },
            new ActionModel { Entity = "Task", Verb = "Read" },
            new ActionModel { Entity = "Task", Verb = "Update" },
            new ActionModel { Entity = "Task", Verb = "Delete" },
            new ActionModel { Entity = "User", Verb = "Read" }
        ]);
        return model;
    }

    private static BeverModel Load(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-model-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "model.json"), json);
        return new BeverModelLoader(
            Options.Create(new BeverOptions { ModelPath = "model.json" }),
            new TestHostEnvironment(directory)).Load();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new NullFileProvider();
        }

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Bever.AspNetCore.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
