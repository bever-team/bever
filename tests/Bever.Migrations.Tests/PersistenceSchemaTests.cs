using System.Text.Json;
using Bever.Core;
using Bever.Migrations;
using Xunit;

namespace Bever.Migrations.Tests;

public sealed class PersistenceSchemaTests
{
    [Fact]
    public void DeserializesMigrationArtifacts()
    {
        var migration = new MigrationArtifact
        {
            Id = "1_initial",
            Name = "initial",
            FromSchemaHash = "from",
            ToSchemaHash = "to",
            Operations = [new MigrationOperation { Kind = "DropEntity", Risk = "Destructive", Entity = "Legacy" }]
        };

        var result = SchemaJson.DeserializeMigration(SchemaJson.Serialize(migration));

        Assert.Equal("1_initial", result.Id);
        Assert.Equal("DropEntity", Assert.Single(result.Operations).Kind);
    }

    [Fact]
    public void ProjectsSystemFieldsEnumsAndRelationships()
    {
        var model = Compile("""
        Entity Customer
            Field Name Text(100)

        Entity Order
            Field Status Enum(Draft, Submitted)
            Field Customer -> Customer OnDelete Cascade
        """);

        var schema = new PersistenceSchemaProjector().Project(model);

        var order = Assert.Single(schema.Entities, entity => entity.Name == "Order");
        Assert.Equal(["Id", "CreatedOn", "CreatedBy", "ModifiedOn", "ModifiedBy", "DeletedOn", "DeletedBy"],
            order.Fields.Where(field => field.System).Select(field => field.Name));
        var status = Assert.Single(order.Fields, field => field.Name == "Status");
        Assert.Equal(["Draft", "Submitted"], status.EnumValues);
        var customer = Assert.Single(order.Fields, field => field.Name == "Customer");
        Assert.Equal("Customer", customer.RelationshipTarget);
        Assert.Equal("Cascade", customer.OnDelete);
    }

    [Fact]
    public void IgnoresActionsAndDeclarationOrderWhenHashing()
    {
        var first = new PersistenceSchemaProjector().Project(Compile("""
        Entity Product
            Field Name Text(100)
            Field Active Boolean

        Action Read Product
            By Anyone
        """));
        var second = new PersistenceSchemaProjector().Project(Compile("""
        Entity Product
            Field Active Boolean
            Field Name Text(100)

        Action Read Product
            By Authenticated
        """));

        Assert.Equal(SchemaJson.Hash(first), SchemaJson.Hash(second));
    }

    [Fact]
    public void IdentityActivationAloneDoesNotChangePersistenceSchemaHash()
    {
        var withoutIdentity = new PersistenceSchemaProjector().Project(Compile("""
        Entity Product
            Field Name Text(100)
        """));
        var withIdentity = new PersistenceSchemaProjector().Project(Compile("""
        Identity

        Entity Product
            Field Name Text(100)
        """));

        Assert.Equal(SchemaJson.Serialize(withoutIdentity), SchemaJson.Serialize(withIdentity));
        Assert.Equal(SchemaJson.Hash(withoutIdentity), SchemaJson.Hash(withIdentity));
    }

    [Fact]
    public void DefaultApplicationRelationshipTargetKindDoesNotChangeCanonicalHashOrDiff()
    {
        var pre0003 = SchemaJson.DeserializeSchema("""
        {
          "formatVersion": "1",
          "entities": [
            {
              "name": "Task",
              "fields": [
                { "name": "Id", "kind": "Primitive", "typeName": "Text", "system": true },
                { "name": "Owner", "kind": "Relationship", "relationshipTarget": "User", "onDelete": "Restrict" }
              ]
            },
            { "name": "User", "fields": [ { "name": "Id", "kind": "Primitive", "typeName": "Text", "system": true } ] }
          ]
        }
        """);
        var post0003 = SchemaJson.DeserializeSchema("""
        {
          "formatVersion": "1",
          "entities": [
            {
              "name": "Task",
              "fields": [
                { "name": "Id", "kind": "Primitive", "typeName": "Text", "system": true },
                { "name": "Owner", "kind": "Relationship", "relationshipTarget": "User", "relationshipTargetKind": "Application", "onDelete": "Restrict" }
              ]
            },
            { "name": "User", "fields": [ { "name": "Id", "kind": "Primitive", "typeName": "Text", "system": true } ] }
          ]
        }
        """);

        Assert.Equal(SchemaJson.Serialize(pre0003), SchemaJson.Serialize(post0003));
        Assert.Equal(SchemaJson.Hash(pre0003), SchemaJson.Hash(post0003));
        Assert.Empty(new SchemaDiffer().Diff(pre0003, post0003));
    }

    [Fact]
    public void AddingIdentityKeepsExistingApplicationRelationshipCanonical()
    {
        var projector = new PersistenceSchemaProjector();
        var previous = projector.Project(Compile("""
        Entity User
            Field Name Text(100)

        Entity Task
            Field Owner -> User
        """));
        var current = projector.Project(Compile("""
        Identity

        Entity User
            Field Name Text(100)

        Entity Task
            Field Owner -> User
        """));

        var owner = current.Entities.Single(entity => entity.Name == "Task").Fields.Single(field => field.Name == "Owner");
        Assert.Equal("Application", owner.RelationshipTargetKind);
        Assert.Equal(SchemaJson.Hash(previous), SchemaJson.Hash(current));
        Assert.Empty(new SchemaDiffer().Diff(previous, current));
    }

    [Fact]
    public void IdentityRelationshipIsApplicationSchemaWithSystemIdentityTargetKind()
    {
        var schema = new PersistenceSchemaProjector().Project(Compile("""
        Identity

        Entity Task
            Field Assignee -> Identity?
        """));

        var field = Assert.Single(schema.Entities.Single().Fields, field => field.Name == "Assignee");
        Assert.Equal("Relationship", field.Kind);
        Assert.Equal("Identity", field.RelationshipTarget);
        Assert.Equal("SystemIdentity", field.RelationshipTargetKind);
    }

    [Fact]
    public void AddingIdentityRelationshipChangesOnlyApplicationRelationshipSchema()
    {
        var projector = new PersistenceSchemaProjector();
        var previous = projector.Project(Compile("""
        Identity

        Entity Task
            Field Title Text(100)
        """));
        var current = projector.Project(Compile("""
        Identity

        Entity Task
            Field Title Text(100)
            Field Assignee -> Identity?
        """));

        var operation = Assert.Single(new SchemaDiffer().Diff(previous, current));

        Assert.Equal("AddField", operation.Kind);
        Assert.Equal("Safe", operation.Risk);
        Assert.Equal("Assignee", operation.Field!.Name);
        Assert.Equal("SystemIdentity", operation.Field.RelationshipTargetKind);
        Assert.DoesNotContain("__bever", SchemaJson.Serialize(current));
    }

    [Fact]
    public void PresentationOnlyVariantsProduceIdenticalSchemasHashesDiffsAndMigrationArtifacts()
    {
        var withoutPresentation = Compile(DomainSource(""));
        var populatedPresentation = Compile(DomainSource("""
            Present
                Label "Product"
                PluralLabel "Products"
                RecordLabel Name
                Field Name
                    Label "Product name"
                Listing
                    Fields Name, Active
                Editing
                    Fields Name, Active
                    Mode Inline
                Actions
                    Update
                        Label "Edit product"
                        Place ListingRow
                    Delete
                        Hidden false
            """));
        var changedAndHiddenPresentation = Compile(DomainSource("""
            Present
                Label "Inventory item"
                PluralLabel "Inventory"
                RecordLabel Name
                Listing
                    Fields Active, Name
                Details
                    Fields Name
                Editing
                    Mode Modal
                Actions
                    Update
                        Hidden true
                    Delete
                        Label "Remove"
                        Hidden true
            """));

        AssertPersistenceEquivalent(withoutPresentation, populatedPresentation);
        AssertPersistenceEquivalent(withoutPresentation, changedAndHiddenPresentation);
    }

    [Fact]
    public void MutationOnlyAndReadPlusMutationModelsProduceIdenticalPersistenceArtifacts()
    {
        var mutationOnly = Compile(DomainSource(""));
        var readPlusMutation = Compile(DomainSource("", "Action Read, Update, Delete Product\n    By Authenticated"));

        AssertPersistenceEquivalent(mutationOnly, readPlusMutation);
    }

    [Fact]
    public void ReadSearchByProducesIdenticalPersistenceArtifacts()
    {
        var withoutRead = Compile(DomainSource("", ""));
        var withSearchBy = Compile(DomainSource("", "Action Read Product\n    By Anyone\n    SearchBy Name"));

        AssertPersistenceEquivalent(withoutRead, withSearchBy);
    }

    [Fact]
    public void NamedPickersAndFieldPickClausesProduceIdenticalPersistenceArtifacts()
    {
        var withoutPicker = Compile(TaskOwnerSource(""));
        var withPicker = Compile(TaskOwnerSource("").Replace("Field Owner -> User", "Field Owner -> User Pick Active")
            + "\nAction Pick User\n    By Anyone\nAction Pick User As Active\n    By Authenticated\n    SearchBy Name");

        Assert.Equal("Active", withPicker.Entities[0].Fields[1].Type!.Picker);
        AssertPersistenceEquivalent(withoutPicker, withPicker);
    }

    [Theory]
    [InlineData("Dropdown")]
    [InlineData("Combobox")]
    [InlineData("Modal")]
    public void PickerDisplayProducesIdenticalPersistenceArtifacts(string display)
    {
        var withoutPicker = Compile(TaskOwnerSource(""));
        var withPicker = Compile(TaskOwnerSource($$"""
            Present
                Creating
                    Mode Page
                    Input Owner
                        Picker {{display}}
            """));

        AssertPersistenceEquivalent(withoutPicker, withPicker);
    }

    [Fact]
    public void SemanticJsonRoundTripWithPresentationPreservesSchemaAndExcludesPresentationProperties()
    {
        var model = Compile(DomainSource("""
            Present
                Label "Product"
                RecordLabel Name
                Listing
                    Fields Name, Active
                Editing
                    Mode Inline
                Actions
                    Update
                        Hidden true
            """));
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        var semanticJson = JsonSerializer.Serialize(model, jsonOptions);
        var roundTrip = JsonSerializer.Deserialize<Bever.Model.BeverModel>(semanticJson, jsonOptions)!;
        var projector = new PersistenceSchemaProjector();
        var schema = projector.Project(model);
        var roundTripSchema = projector.Project(roundTrip);
        var schemaJson = SchemaJson.Serialize(roundTripSchema);

        Assert.Contains("\"presentation\"", semanticJson);
        Assert.Equal(SchemaJson.Serialize(schema), schemaJson);
        Assert.Equal(SchemaJson.Hash(schema), SchemaJson.Hash(roundTripSchema));
        AssertNoPresentationProperties(schemaJson);
    }

    [Fact]
    public void RejectsTypesTheProviderCannotStore()
    {
        var model = Compile("""
        Entity Product
            Field Name Text(50)
        """);
        model.Entities[0].Fields[0].Type!.Name = "Image";

        var exception = Assert.Throws<MigrationException>(() => new PersistenceSchemaProjector().Project(model));

        Assert.Contains("unsupported primitive type 'Image'", exception.Message);
    }

    [Fact]
    public void InitialSchemaCreatesEntitiesWithoutBackfills()
    {
        var current = new PersistenceSchemaProjector().Project(Compile("""
        Entity Product
            Field Name Text(100)
        """));

        var operation = Assert.Single(new SchemaDiffer().Diff(new PersistenceSchema(), current));

        Assert.Equal("CreateEntity", operation.Kind);
        Assert.Equal("Safe", operation.Risk);
    }

    [Fact]
    public void AddingRequiredFieldRequiresBackfill()
    {
        var projector = new PersistenceSchemaProjector();
        var previous = projector.Project(Compile("""
        Entity Product
            Field Name Text(100)
        """));
        var current = projector.Project(Compile("""
        Entity Product
            Field Name Text(100)
            Field Sku Text(30)
        """));

        var operation = Assert.Single(new SchemaDiffer().Diff(previous, current));

        Assert.Equal("AddField", operation.Kind);
        Assert.Equal("RequiresBackfill", operation.Risk);
    }

    [Theory]
    [InlineData("Field Name Text(100)", "Field Name Text(200)", "Safe")]
    [InlineData("Field Name Text(100)", "Field Name Text", "Safe")]
    [InlineData("Field Name Text(100)", "Field Name Text(50)", "Destructive")]
    [InlineData("Field Level Enum(Low, High)", "Field Level Enum(Low, Medium, High)", "Safe")]
    [InlineData("Field Level Enum(Low, Medium, High)", "Field Level Enum(Low, High)", "Destructive")]
    [InlineData("Field Owner -> Owner?", "Field Owner -> Owner? OnDelete SetNull", "Safe")]
    [InlineData("Field Name Text(100)", "Field Name Text(100)?", "Safe")]
    [InlineData("Field Name Text(100)?", "Field Name Text(100)", "RequiresBackfill")]
    [InlineData("Field Count Int", "Field Count Decimal", "Destructive")]
    public void ClassifiesFieldChangesByWhetherExistingValuesStayValid(string before, string after, string risk)
    {
        static PersistenceSchema Project(string field) => new PersistenceSchemaProjector().Project(Compile($"Entity Product\n    {field}\n\nEntity Owner\n    Field Name Text(100)\n"));

        var operation = Assert.Single(new SchemaDiffer().Diff(Project(before), Project(after)));

        Assert.Equal("AlterField", operation.Kind);
        Assert.Equal(risk, operation.Risk);
    }

    [Fact]
    public void SnapshotRoundTripPreservesHash()
    {
        var schema = new PersistenceSchemaProjector().Project(Compile("""
        Entity Product
            Field Name Text(100)
        """));

        var roundTrip = SchemaJson.DeserializeSchema(SchemaJson.Serialize(schema));

        Assert.Equal(SchemaJson.Hash(schema), SchemaJson.Hash(roundTrip));
    }

    [Fact]
    public void SemanticModelRoundTripPreservesSchemaHash()
    {
        var model = Compile("""
        Entity Product
            Field Name Text(100)
        """);
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        var roundTripModel = JsonSerializer.Deserialize<Bever.Model.BeverModel>(
            JsonSerializer.Serialize(model, jsonOptions),
            jsonOptions)!;
        var projector = new PersistenceSchemaProjector();
        var schema = projector.Project(model);
        var roundTripSchema = projector.Project(roundTripModel);

        Assert.Equal(100, roundTripSchema.Entities.Single().Fields.Single(field => field.Name == "Name").MaxLength);
        Assert.Equal(SchemaJson.Hash(schema), SchemaJson.Hash(roundTripSchema));
    }

    [Fact]
    public void ParsesTypedBackfillLiterals()
    {
        var integer = MigrationLiteralParser.Parse("Product.Stock", new PersistenceField
        {
            Name = "Stock",
            Kind = "Primitive",
            TypeName = "Int"
        }, "42");
        var option = MigrationLiteralParser.Parse("Product.Status", new PersistenceField
        {
            Name = "Status",
            Kind = "Enum",
            EnumValues = ["Draft", "Active"]
        }, "Active");

        Assert.Equal("Integer", integer.Kind);
        Assert.Equal(42, integer.Value);
        Assert.Equal("Text", option.Kind);
        Assert.Equal("Active", option.Value);
    }

    [Fact]
    public void RejectsInvalidBackfillLiteral()
    {
        var field = new PersistenceField { Name = "Active", Kind = "Primitive", TypeName = "Boolean" };

        var exception = Assert.Throws<MigrationException>(() => MigrationLiteralParser.Parse("Product.Active", field, "yes"));

        Assert.Contains("True or False", exception.Message);
    }

    private static Bever.Model.BeverModel Compile(string source)
    {
        var result = new BeverCompiler().Compile([new SourceText("inline.bev", source)]);
        Assert.True(result.Success);
        return result.Model;
    }

    private static string DomainSource(string presentation, string actions = "Action Update, Delete Product\n    By Authenticated") =>
        "Entity Product\n"
        + "    Field Name Text(100)\n"
        + "    Field Active Boolean\n"
        + (presentation.Length == 0 ? string.Empty : $"    {presentation.Replace("\n", "\n    ")}\n")
        + $"\n{actions}";

    private static string TaskOwnerSource(string presentation) =>
        "Entity Task\n"
        + "    Field Name Text(100)\n"
        + "    Field Owner -> User\n"
        + (presentation.Length == 0 ? string.Empty : $"    {presentation.Replace("\n", "\n    ")}\n")
        + "\nEntity User\n"
        + "    Field Name Text(100)\n"
        + "\nAction Create Task\n"
        + "    By Authenticated\n"
        + "Action Read User\n"
        + "    By Anyone";

    private static void AssertPersistenceEquivalent(
        Bever.Model.BeverModel expectedModel,
        Bever.Model.BeverModel actualModel)
    {
        var projector = new PersistenceSchemaProjector();
        var expected = projector.Project(expectedModel);
        var actual = projector.Project(actualModel);
        var differ = new SchemaDiffer();

        Assert.Equal(SchemaJson.Serialize(expected), SchemaJson.Serialize(actual));
        Assert.Equal(SchemaJson.Hash(expected), SchemaJson.Hash(actual));
        Assert.Empty(differ.Diff(expected, actual));
        Assert.Empty(differ.Diff(actual, expected));

        var empty = new PersistenceSchema();
        var expectedArtifact = new MigrationArtifact
        {
            Id = "isolation",
            Name = "isolation",
            FromSchemaHash = SchemaJson.Hash(empty),
            ToSchemaHash = SchemaJson.Hash(expected),
            Operations = differ.Diff(empty, expected).ToList()
        };
        var actualArtifact = new MigrationArtifact
        {
            Id = "isolation",
            Name = "isolation",
            FromSchemaHash = SchemaJson.Hash(empty),
            ToSchemaHash = SchemaJson.Hash(actual),
            Operations = differ.Diff(empty, actual).ToList()
        };

        var expectedArtifactJson = SchemaJson.Serialize(expectedArtifact);
        var actualArtifactJson = SchemaJson.Serialize(actualArtifact);
        Assert.All(expectedArtifact.Operations, operation => Assert.Equal("Safe", operation.Risk));
        Assert.Equal(expectedArtifactJson, actualArtifactJson);
        AssertNoPresentationProperties(actualArtifactJson);
    }

    private static void AssertNoPresentationProperties(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.DoesNotContain(
            EnumeratePropertyNames(document.RootElement),
            name => PresentationPropertyNames.Contains(name));
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var descendant in EnumeratePropertyNames(property.Value))
                {
                    yield return descendant;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var descendant in EnumeratePropertyNames(item))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static readonly HashSet<string> PresentationPropertyNames = new(StringComparer.Ordinal)
    {
        "presentation", "label", "pluralLabel", "recordLabel", "listing", "details", "creating", "editing",
        "actionOverrides", "inputs", "widget", "display", "mode", "emphasis", "confirm", "placements", "order", "hidden"
    };
}
