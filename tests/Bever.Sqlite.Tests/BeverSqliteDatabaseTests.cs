using System.Text.Json.Nodes;
using Bever.Core;
using Bever.Migrations;
using Microsoft.Data.Sqlite;

namespace Bever.Sqlite.Tests;

public sealed class BeverSqliteDatabaseTests
{
    [Fact]
    public async Task CreatesSingleDatabaseAndReportsCurrentSchema()
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateSchema());

        Assert.Equal(BeverSqliteSchemaStatus.Uninitialized, await database.GetSchemaStatusAsync());

        await database.EnsureCreatedAsync();

        Assert.True(File.Exists(path));
        Assert.Equal(BeverSqliteSchemaStatus.Current, await database.GetSchemaStatusAsync());
    }

    [Fact]
    public async Task ReadOnlyStatusDoesNotCreateDatabaseFileOrDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-sqlite-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, ".bever", "bever.db");
        var database = new BeverSqliteDatabase(path, CreateSchema(), createIfMissing: false);

        Assert.Equal(BeverSqliteSchemaStatus.Uninitialized, await database.GetSchemaStatusAsync());
        Assert.Equal(BeverSqliteIdentitySchemaStatus.Uninitialized, await database.GetIdentitySchemaStatusAsync());
        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task PersistsCrudAcrossDatabaseInstances()
    {
        var path = NewDatabasePath();
        var schema = CreateSchema();
        var database = new BeverSqliteDatabase(path, schema);
        await database.EnsureCreatedAsync();

        var created = await database.CreateAsync("Product", new JsonObject
        {
            ["name"] = "Widget",
            ["active"] = true,
            ["price"] = 12.34m,
            ["note"] = null
        }, default);
        var id = created["id"]!.GetValue<string>();

        Assert.Equal(32, id.Length);
        Assert.NotNull(created["createdOn"]);
        Assert.Equal(created["createdOn"]!.GetValue<string>(), created["modifiedOn"]!.GetValue<string>());
        Assert.Equal(12.34m, created["price"]!.GetValue<decimal>());

        database = new BeverSqliteDatabase(path, schema);
        var persisted = await database.GetAsync("Product", id, default);
        Assert.Equal("Widget", persisted!["name"]!.GetValue<string>());

        var patched = await database.PatchAsync("Product", id, new JsonObject { ["name"] = "Updated" }, default);
        Assert.Equal("Updated", patched!["name"]!.GetValue<string>());
        Assert.True(string.CompareOrdinal(patched["modifiedOn"]!.GetValue<string>(), created["modifiedOn"]!.GetValue<string>()) >= 0);

        Assert.True(await database.DeleteAsync("Product", id, default));
        Assert.False(await database.DeleteAsync("Product", id, default));
        Assert.Null(await database.GetAsync("Product", id, default));
        Assert.Empty(await database.ListAsync("Product", default));
    }

    [Fact]
    public async Task PersistsRuntimeActorSystemFields()
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateSchema());
        await database.EnsureCreatedAsync();

        var created = await database.CreateAsync("Product", new JsonObject
        {
            ["name"] = "Widget",
            ["active"] = true,
            ["price"] = 12.34m,
            ["createdBy"] = "idn_actor",
            ["modifiedBy"] = "idn_actor"
        }, default);
        var id = created["id"]!.GetValue<string>();
        var patched = await database.PatchAsync("Product", id, new JsonObject
        {
            ["name"] = "Updated",
            ["modifiedBy"] = "idn_editor"
        }, default);
        await database.PatchAsync("Product", id, new JsonObject
        {
            ["modifiedBy"] = "idn_deleter",
            ["deletedBy"] = "idn_deleter"
        }, default);
        await database.DeleteAsync("Product", id, default);

        Assert.Equal("idn_actor", created["createdBy"]!.GetValue<string>());
        Assert.Equal("idn_actor", created["modifiedBy"]!.GetValue<string>());
        Assert.Equal("idn_editor", patched!["modifiedBy"]!.GetValue<string>());

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"CreatedBy\", \"ModifiedBy\", \"DeletedBy\" FROM \"Product\" WHERE \"Id\" = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("idn_actor", reader.GetString(0));
        Assert.Equal("idn_deleter", reader.GetString(1));
        Assert.Equal("idn_deleter", reader.GetString(2));
    }

    [Theory]
    [InlineData("populated-presentation")]
    [InlineData("changed-hidden-presentation")]
    [InlineData("read-plus-mutation")]
    public async Task PresentationAndReadActionVariantsPreserveSqliteBehavior(string variant)
    {
        var projector = new PersistenceSchemaProjector();
        var baselineSchema = projector.Project(Compile(PersistenceIsolationSource("mutation-only")));
        var variantSchema = projector.Project(Compile(PersistenceIsolationSource(variant)));

        Assert.Equal(SchemaJson.Serialize(baselineSchema), SchemaJson.Serialize(variantSchema));
        Assert.Equal(SchemaJson.Hash(baselineSchema), SchemaJson.Hash(variantSchema));

        var baseline = await ExercisePersistenceBehaviorAsync(baselineSchema);
        var actual = await ExercisePersistenceBehaviorAsync(variantSchema);

        Assert.Equal(baseline, actual);
        Assert.Equal(BeverSqliteSchemaStatus.Current, actual.InitialStatus);
        Assert.Equal(BeverSqliteSchemaStatus.Current, actual.FinalStatus);
        Assert.Equal(actual.ExpectedHash, actual.StoredHash);
        Assert.Equal("Widget", actual.CreatedName);
        Assert.True(actual.CreatedActive);
        Assert.Equal(1, actual.ListCount);
        Assert.Equal("Updated", actual.PatchedName);
        Assert.True(actual.Deleted);
        Assert.True(actual.MissingAfterDelete);
    }

    [Fact]
    public async Task DetectsPhysicalSchemaDrift()
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateSchema());
        await database.EnsureCreatedAsync();

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE Product ADD COLUMN Unexpected TEXT";
        await command.ExecuteNonQueryAsync();

        Assert.Equal(BeverSqliteSchemaStatus.SchemaDrift, await database.GetSchemaStatusAsync());
    }

    [Fact]
    public async Task DetectsMissingCheckConstraint()
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateSchema());
        await database.EnsureCreatedAsync();

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'Product'";
        var tableSql = (string)(await read.ExecuteScalarAsync())!;
        var replacementSql = tableSql
            .Replace("\"Product\"", "\"ProductReplacement\"", StringComparison.Ordinal)
            .Replace(" CHECK (\"Active\" IN (0, 1))", string.Empty, StringComparison.Ordinal);
        await using var rebuild = connection.CreateCommand();
        rebuild.CommandText = $"""
            {replacementSql};
            INSERT INTO ProductReplacement SELECT * FROM Product;
            DROP TABLE Product;
            ALTER TABLE ProductReplacement RENAME TO Product;
            """;
        await rebuild.ExecuteNonQueryAsync();

        Assert.Equal(BeverSqliteSchemaStatus.SchemaDrift, await database.GetSchemaStatusAsync());
    }

    [Fact]
    public async Task DetectsMissingRelationshipIndex()
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateRelationshipSchema("Restrict", false));
        await database.EnsureCreatedAsync();

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP INDEX IX_Child_Parent";
        await command.ExecuteNonQueryAsync();

        Assert.Equal(BeverSqliteSchemaStatus.SchemaDrift, await database.GetSchemaStatusAsync());
    }

    [Fact]
    public async Task AppliesRequiredFieldMigrationWithLiteralBackfill()
    {
        var path = NewDatabasePath();
        var previous = CreateSchema();
        var initialDatabase = new BeverSqliteDatabase(path, previous);
        await initialDatabase.EnsureCreatedAsync();
        var created = await initialDatabase.CreateAsync("Product", new JsonObject
        {
            ["name"] = "Widget",
            ["active"] = true,
            ["price"] = 12.34m,
            ["note"] = null
        }, default);

        var target = SchemaJson.DeserializeSchema(SchemaJson.Serialize(previous));
        target.Entities.Single().Fields.Add(new PersistenceField
        {
            Name = "Currency",
            Kind = "Enum",
            Nullable = false,
            EnumValues = ["USD", "EUR"]
        });
        var operations = new SchemaDiffer().Diff(previous, target).ToList();
        operations.Single().Backfill = new MigrationLiteral { Kind = "Enum", Value = "USD" };
        var migration = new MigrationArtifact
        {
            Id = "20260727000000_add-currency",
            Name = "add-currency",
            FromSchemaHash = SchemaJson.Hash(previous),
            ToSchemaHash = SchemaJson.Hash(target),
            Operations = operations
        };

        var migratedDatabase = new BeverSqliteDatabase(path, target);
        await migratedDatabase.ApplyMigrationAsync(migration);

        Assert.Equal(BeverSqliteSchemaStatus.Current, await migratedDatabase.GetSchemaStatusAsync());
        var record = await migratedDatabase.GetAsync("Product", created["id"]!.GetValue<string>(), default);
        Assert.Equal("USD", record!["currency"]!.GetValue<string>());
    }

    [Fact]
    public async Task RejectsMigrationWithWrongBeforeHash()
    {
        var path = NewDatabasePath();
        var schema = CreateSchema();
        var database = new BeverSqliteDatabase(path, schema);
        await database.EnsureCreatedAsync();
        var migration = new MigrationArtifact
        {
            Id = "invalid",
            Name = "invalid",
            FromSchemaHash = "wrong",
            ToSchemaHash = SchemaJson.Hash(schema)
        };

        var exception = await Assert.ThrowsAsync<MigrationException>(() => database.ApplyMigrationAsync(migration));

        Assert.Contains("expects schema hash", exception.Message);
    }

    [Fact]
    public async Task RestrictPreventsLogicalDeleteWhenActiveDependentsExist()
    {
        var database = new BeverSqliteDatabase(NewDatabasePath(), CreateRelationshipSchema("Restrict", false));
        await database.EnsureCreatedAsync();
        var parent = await database.CreateAsync("Parent", new JsonObject(), default);
        await database.CreateAsync("Child", new JsonObject { ["parent"] = parent["id"]!.GetValue<string>() }, default);

        await Assert.ThrowsAsync<BeverSqliteDeleteRestrictedException>(() =>
            database.DeleteAsync("Parent", parent["id"]!.GetValue<string>(), default));

        Assert.NotNull(await database.GetAsync("Parent", parent["id"]!.GetValue<string>(), default));
    }

    [Fact]
    public async Task RestrictRollbackDoesNotMutateEarlierCascadeRecords()
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateCascadeThenRestrictSchema());
        await database.EnsureCreatedAsync();
        var parent = await database.CreateAsync("Parent", new JsonObject(), default);
        var child = await database.CreateAsync("Child", new JsonObject { ["parent"] = parent["id"]!.GetValue<string>() }, default);
        await database.CreateAsync("Grandchild", new JsonObject { ["child"] = child["id"]!.GetValue<string>() }, default);

        await Assert.ThrowsAsync<BeverSqliteDeleteRestrictedException>(() =>
            database.DeleteAsync("Parent", parent["id"]!.GetValue<string>(), "idn_deleter", default));

        Assert.Null(await ReadScalarAsync(path, "SELECT \"DeletedOn\" FROM \"Parent\" WHERE \"Id\" = $id", parent["id"]!.GetValue<string>()));
        Assert.Null(await ReadScalarAsync(path, "SELECT \"DeletedOn\" FROM \"Child\" WHERE \"Id\" = $id", child["id"]!.GetValue<string>()));
        Assert.Null(await ReadScalarAsync(path, "SELECT \"ModifiedBy\" FROM \"Child\" WHERE \"Id\" = $id", child["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task DeleteTransactionSetsActorOnTargetAndCascadedSoftDeletes()
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateRelationshipSchema("Cascade", false));
        await database.EnsureCreatedAsync();
        var parent = await database.CreateAsync("Parent", new JsonObject(), default);
        var child = await database.CreateAsync("Child", new JsonObject { ["parent"] = parent["id"]!.GetValue<string>() }, default);

        Assert.True(await database.DeleteAsync("Parent", parent["id"]!.GetValue<string>(), "idn_deleter", default));

        Assert.Equal("idn_deleter", await ReadScalarAsync(path, "SELECT \"DeletedBy\" FROM \"Parent\" WHERE \"Id\" = $id", parent["id"]!.GetValue<string>()));
        Assert.Equal("idn_deleter", await ReadScalarAsync(path, "SELECT \"ModifiedBy\" FROM \"Parent\" WHERE \"Id\" = $id", parent["id"]!.GetValue<string>()));
        Assert.Equal("idn_deleter", await ReadScalarAsync(path, "SELECT \"DeletedBy\" FROM \"Child\" WHERE \"Id\" = $id", child["id"]!.GetValue<string>()));
        Assert.Equal("idn_deleter", await ReadScalarAsync(path, "SELECT \"ModifiedBy\" FROM \"Child\" WHERE \"Id\" = $id", child["id"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("Cascade")]
    [InlineData("SetNull")]
    public async Task AppliesLogicalRelationshipDeleteBehavior(string behavior)
    {
        var database = new BeverSqliteDatabase(NewDatabasePath(), CreateRelationshipSchema(behavior, behavior == "SetNull"));
        await database.EnsureCreatedAsync();
        var parent = await database.CreateAsync("Parent", new JsonObject(), default);
        var child = await database.CreateAsync("Child", new JsonObject { ["parent"] = parent["id"]!.GetValue<string>() }, default);

        Assert.True(await database.DeleteAsync("Parent", parent["id"]!.GetValue<string>(), default));

        var persistedChild = await database.GetAsync("Child", child["id"]!.GetValue<string>(), default);
        if (behavior == "Cascade")
        {
            Assert.Null(persistedChild);
        }
        else
        {
            Assert.NotNull(persistedChild);
            Assert.Null(persistedChild!["parent"]);
        }
    }

    [Fact]
    public async Task InitializesPrivateIdentitySchemaSeparatelyFromApplicationSchema()
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateSchema());
        await database.EnsureCreatedAsync();
        var appHash = await database.GetCurrentSchemaHashAsync();

        Assert.Equal(BeverSqliteIdentitySchemaStatus.Uninitialized, await database.GetIdentitySchemaStatusAsync());

        await database.EnsureIdentitySchemaCreatedAsync();

        Assert.Equal(BeverSqliteIdentitySchemaStatus.Current, await database.GetIdentitySchemaStatusAsync());
        Assert.Equal(appHash, await database.GetCurrentSchemaHashAsync());
        Assert.Equal(BeverSqliteSchemaStatus.Current, await database.GetSchemaStatusAsync());
    }

    [Fact]
    public async Task PersistsPrivateIdentityRecordsCredentialsAdministratorsGrantsAndAudit()
    {
        var database = new BeverSqliteDatabase(NewDatabasePath(), CreateSchema());
        await database.ApplyIdentitySchemaAsync();

        var identity = await database.RegisterIdentityAsync(" Ada@Example.com ", "correct horse battery staple", default);
        await database.AssignAdministratorAsync(identity.Id, "bootstrap", default);
        var grantId = await database.AddDirectGrantAsync(identity.Id, "Product", "Read", null, "grant", default);

        Assert.Equal("ada@example.com", identity.NormalizedEmail);
        Assert.True(await database.VerifyIdentityPasswordAsync(identity.Id, "correct horse battery staple", default));
        Assert.False(await database.VerifyIdentityPasswordAsync(identity.Id, "wrong", default));
        Assert.True(await database.IsAdministratorAsync(identity.Id, default));
        Assert.Equal(grantId, Assert.Single(await database.ListDirectGrantsAsync(identity.Id, default)).Id);
        Assert.Contains(await database.ListIdentityAuditAsync(default), entry => entry.Operation == "grant" && entry.IdentityId == identity.Id);
    }

    [Fact]
    public async Task SetsRolesIgnoringCaseAndAuditsEachChange()
    {
        var database = new BeverSqliteDatabase(NewDatabasePath(), CreateSchema());
        await database.ApplyIdentitySchemaAsync();
        var ada = await database.RegisterIdentityAsync("ada@example.com", "correct horse battery staple", default);
        var bob = await database.RegisterIdentityAsync("bob@example.com", "correct horse battery staple", default);

        await database.SetRolesAsync(ada.Id, ["Manager", "Agent"], null, default);
        await database.SetRolesAsync(ada.Id, ["AGENT", "Auditor"], bob.Id, default);

        Assert.Equal(["Agent", "Auditor"], await database.ListRolesAsync(ada.Id, default));
        Assert.Empty(await database.ListRolesAsync(bob.Id, default));
        var audit = (await database.ListIdentityAuditAsync(default)).Where(entry => entry.Operation.StartsWith("identity.role.", StringComparison.Ordinal)).ToList();
        // Entries written in the same tick have no defined order.
        Assert.Equal(
            new[]
            {
                ("identity.role.add", """{"role":"Agent","by":null}"""),
                ("identity.role.add", $$"""{"role":"Auditor","by":"{{bob.Id}}"}"""),
                ("identity.role.add", """{"role":"Manager","by":null}"""),
                ("identity.role.revoke", $$"""{"role":"Manager","by":"{{bob.Id}}"}""")
            },
            audit.Select(entry => (entry.Operation, entry.MetadataJson)).Order());
        Assert.All(audit, entry => Assert.Equal(ada.Id, entry.IdentityId));
    }

    [Fact]
    public async Task RegistersBootstrapAdministratorAtomically()
    {
        var database = new BeverSqliteDatabase(NewDatabasePath(), CreateSchema());
        await database.EnsureIdentitySchemaCreatedAsync();

        var identity = await database.RegisterBootstrapAdministratorAsync("admin@example.com", "password", "bootstrap", default);

        Assert.True(await database.VerifyIdentityPasswordAsync(identity.Id, "password", default));
        Assert.True(await database.IsAdministratorAsync(identity.Id, default));
        Assert.Contains(await database.ListIdentityAuditAsync(default), entry => entry.Operation == "bootstrap" && entry.IdentityId == identity.Id);
    }

    [Fact]
    public async Task BootstrapAdministratorRefusesExistingAdministratorWithoutCreatingIdentity()
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateSchema());
        await database.EnsureIdentitySchemaCreatedAsync();
        await database.RegisterBootstrapAdministratorAsync("admin@example.com", "password", "bootstrap", default);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.RegisterBootstrapAdministratorAsync("second@example.com", "password", "bootstrap", default));

        Assert.Equal("Bootstrap refused because an Administrator already exists.", exception.Message);
        Assert.Equal(1L, await ReadCountAsync(path, "__bever_identities"));
        Assert.Equal(1L, await ReadCountAsync(path, "__bever_identity_administrators"));
        Assert.Equal(1L, await ReadCountAsync(path, "__bever_identity_audit"));
    }

    [Fact]
    public async Task DetectsPrivateIdentitySchemaDrift()
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateSchema());
        await database.EnsureIdentitySchemaCreatedAsync();

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE __bever_identity_grants";
        await command.ExecuteNonQueryAsync();

        Assert.Equal(BeverSqliteIdentitySchemaStatus.SchemaDrift, await database.GetIdentitySchemaStatusAsync());
    }

    [Fact]
    public async Task DetectsOrphanPrivateIdentityTablesAsDrift()
    {
        var path = NewDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE \"__bever_identities\" (\"Id\" TEXT PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }

        var database = new BeverSqliteDatabase(path, CreateSchema(), createIfMissing: false);

        Assert.Equal(BeverSqliteIdentitySchemaStatus.SchemaDrift, await database.GetIdentitySchemaStatusAsync());
    }

    [Fact]
    public async Task DetectsPrivateIdentitySchemaIndexDrift()
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateSchema());
        await database.EnsureIdentitySchemaCreatedAsync();

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP INDEX IX___bever_identity_grants_identity";
        await command.ExecuteNonQueryAsync();

        Assert.Equal(BeverSqliteIdentitySchemaStatus.SchemaDrift, await database.GetIdentitySchemaStatusAsync());
    }

    [Theory]
    [InlineData("__bever_identity_extra")]
    [InlineData("__bever_identities_backup")]
    public async Task DetectsUnexpectedPrivateIdentityArtifactDriftWhenKnownSchemaIsCurrent(string artifactName)
    {
        var path = NewDatabasePath();
        var database = new BeverSqliteDatabase(path, CreateSchema());
        await database.EnsureIdentitySchemaCreatedAsync();

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE \"{artifactName}\" (\"Id\" TEXT PRIMARY KEY)";
        await command.ExecuteNonQueryAsync();

        Assert.Equal(BeverSqliteIdentitySchemaStatus.SchemaDrift, await database.GetIdentitySchemaStatusAsync());
    }

    [Fact]
    public async Task PrivateIdentitySchemaDoesNotInitializeApplicationSchema()
    {
        var database = new BeverSqliteDatabase(NewDatabasePath(), CreateSchema());

        await database.EnsureIdentitySchemaCreatedAsync();

        Assert.Equal(BeverSqliteIdentitySchemaStatus.Current, await database.GetIdentitySchemaStatusAsync());
        Assert.Equal(BeverSqliteSchemaStatus.Uninitialized, await database.GetSchemaStatusAsync());
        Assert.Null(await database.GetCurrentSchemaHashAsync());
    }

    [Fact]
    public async Task CreatesSystemIdentityRelationshipsWithoutApplicationForeignKey()
    {
        var database = new BeverSqliteDatabase(NewDatabasePath(), CreateIdentityRelationshipSchema());

        await database.EnsureCreatedAsync();
        var created = await database.CreateAsync("Task", new JsonObject { ["assignee"] = "idn_missing" }, default);

        Assert.Equal("idn_missing", created["assignee"]!.GetValue<string>());
    }

    private static PersistenceSchema CreateSchema() => new()
    {
        Entities =
        [
            new PersistenceEntity
            {
                Name = "Product",
                Fields =
                [
                    SystemField("Id", "Text", false),
                    SystemField("CreatedOn", "DateTime", false),
                    SystemField("CreatedBy", "Text", true),
                    SystemField("ModifiedOn", "DateTime", false),
                    SystemField("ModifiedBy", "Text", true),
                    SystemField("DeletedOn", "DateTime", true),
                    SystemField("DeletedBy", "Text", true),
                    new PersistenceField { Name = "Name", Kind = "Primitive", TypeName = "Text", Nullable = false, MaxLength = 100 },
                    new PersistenceField { Name = "Active", Kind = "Primitive", TypeName = "Boolean", Nullable = false },
                    new PersistenceField { Name = "Price", Kind = "Primitive", TypeName = "Decimal", Nullable = false },
                    new PersistenceField { Name = "Note", Kind = "Primitive", TypeName = "Text", Nullable = true }
                ]
            }
        ]
    };

    private static PersistenceField SystemField(string name, string typeName, bool nullable) => new()
    {
        Name = name,
        Kind = "Primitive",
        TypeName = typeName,
        Nullable = nullable,
        System = true
    };

    private static PersistenceSchema CreateRelationshipSchema(string onDelete, bool nullable) => new()
    {
        Entities =
        [
            new PersistenceEntity { Name = "Parent", Fields = SystemFields() },
            new PersistenceEntity
            {
                Name = "Child",
                Fields =
                [
                    .. SystemFields(),
                    new PersistenceField
                    {
                        Name = "Parent",
                        Kind = "Relationship",
                        Nullable = nullable,
                        RelationshipTarget = "Parent",
                        OnDelete = onDelete
                    }
                ]
            }
        ]
    };

    private static PersistenceSchema CreateIdentityRelationshipSchema() => new()
    {
        Entities =
        [
            new PersistenceEntity
            {
                Name = "Task",
                Fields =
                [
                    .. SystemFields(),
                    new PersistenceField
                    {
                        Name = "Assignee",
                        Kind = "Relationship",
                        Nullable = true,
                        RelationshipTarget = "Identity",
                        RelationshipTargetKind = "SystemIdentity"
                    }
                ]
            }
        ]
    };

    private static PersistenceSchema CreateCascadeThenRestrictSchema() => new()
    {
        Entities =
        [
            new PersistenceEntity { Name = "Parent", Fields = SystemFields() },
            new PersistenceEntity
            {
                Name = "Child",
                Fields =
                [
                    .. SystemFields(),
                    new PersistenceField { Name = "Parent", Kind = "Relationship", Nullable = false, RelationshipTarget = "Parent", OnDelete = "Cascade" }
                ]
            },
            new PersistenceEntity
            {
                Name = "Grandchild",
                Fields =
                [
                    .. SystemFields(),
                    new PersistenceField { Name = "Child", Kind = "Relationship", Nullable = false, RelationshipTarget = "Child", OnDelete = "Restrict" }
                ]
            }
        ]
    };

    private static List<PersistenceField> SystemFields() =>
    [
        SystemField("Id", "Text", false),
        SystemField("CreatedOn", "DateTime", false),
        SystemField("CreatedBy", "Text", true),
        SystemField("ModifiedOn", "DateTime", false),
        SystemField("ModifiedBy", "Text", true),
        SystemField("DeletedOn", "DateTime", true),
        SystemField("DeletedBy", "Text", true)
    ];

    private static string NewDatabasePath() => Path.Combine(
        Path.GetTempPath(),
        "bever-sqlite-tests",
        Guid.NewGuid().ToString("N"),
        "bever.db");

    private static async Task<object?> ReadScalarAsync(string path, string sql, string id)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task<long> ReadCountAsync(string path, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<PersistenceBehavior> ExercisePersistenceBehaviorAsync(PersistenceSchema schema)
    {
        var database = new BeverSqliteDatabase(NewDatabasePath(), schema);
        await database.EnsureCreatedAsync();
        var initialStatus = await database.GetSchemaStatusAsync();
        var storedHash = await database.GetCurrentSchemaHashAsync();
        var created = await database.CreateAsync("Product", new JsonObject
        {
            ["name"] = "Widget",
            ["active"] = true
        }, default);
        var id = created["id"]!.GetValue<string>();
        var listCount = (await database.ListAsync("Product", default)).Count;
        var patched = await database.PatchAsync("Product", id, new JsonObject { ["name"] = "Updated" }, default);
        var deleted = await database.DeleteAsync("Product", id, default);
        var missingAfterDelete = await database.GetAsync("Product", id, default) is null;

        return new PersistenceBehavior(
            database.SchemaHash,
            storedHash,
            initialStatus,
            await database.GetSchemaStatusAsync(),
            created["name"]!.GetValue<string>(),
            created["active"]!.GetValue<bool>(),
            listCount,
            patched!["name"]!.GetValue<string>(),
            deleted,
            missingAfterDelete);
    }

    private static Bever.Model.BeverModel Compile(string source)
    {
        var result = new BeverCompiler().Compile([new SourceText("persistence-isolation.bev", source)]);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Model;
    }

    private static string PersistenceIsolationSource(string variant)
    {
        var presentation = variant switch
        {
            "populated-presentation" => """
                Present
                    Label "Product"
                    RecordLabel Name
                    Listing
                        Fields Name, Active
                    Editing
                        Fields Name, Active
                        Mode Inline
                    Actions
                        Update
                            Place ListingRow
                        Delete
                            Hidden false
                """,
            "changed-hidden-presentation" => """
                Present
                    Label "Inventory item"
                    PluralLabel "Inventory"
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
                """,
            _ => string.Empty
        };
        var actions = variant == "read-plus-mutation"
            ? "Action Read, Update, Delete Product\n    By Authenticated"
            : "Action Update, Delete Product\n    By Authenticated";
        return "Entity Product\n"
            + "    Field Name Text(100)\n"
            + "    Field Active Boolean\n"
            + (presentation.Length == 0 ? string.Empty : $"    {presentation.Replace("\n", "\n    ")}\n")
            + $"\n{actions}";
    }

    private sealed record PersistenceBehavior(
        string ExpectedHash,
        string? StoredHash,
        BeverSqliteSchemaStatus InitialStatus,
        BeverSqliteSchemaStatus FinalStatus,
        string CreatedName,
        bool CreatedActive,
        int ListCount,
        string PatchedName,
        bool Deleted,
        bool MissingAfterDelete);
}
