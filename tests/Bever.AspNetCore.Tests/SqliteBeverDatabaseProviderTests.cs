using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Json;
using Bever.Migrations;
using Bever.Model;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bever.AspNetCore.Tests;

public sealed class SqliteBeverDatabaseProviderTests
{
    [Fact]
    public async Task AddBeverUsesFileBackedSqliteByDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-aspnetcore-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var modelPath = Path.Combine(directory, "model.json");
        var databasePath = Path.Combine(directory, ".bever", "bever.db");
        File.WriteAllText(modelPath, JsonSerializer.Serialize(CreateModel(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = directory,
            EnvironmentName = Environments.Development
        });
        builder.Services.AddBever(options =>
        {
            options.ModelPath = "model.json";
            options.DatabasePath = databasePath;
        });
        await using var app = builder.Build();

        Assert.IsType<SqliteBeverDatabaseProvider>(app.Services.GetRequiredService<IBeverDatabaseProvider>());
        await app.Services.GetRequiredService<Bever.Sqlite.BeverSqliteDatabase>().EnsureCreatedAsync();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        var provider = app.Services.GetRequiredService<IBeverDatabaseProvider>();
        var entity = app.Services.GetRequiredService<BeverRuntime>().Entities.Single();
        var created = await provider.CreateAsync(entity, new JsonObject { ["name"] = "Widget" }, default);

        Assert.True(File.Exists(databasePath));
        Assert.Equal("Widget", created["name"]!.GetValue<string>());
        Assert.NotNull(created["createdOn"]);
    }

    [Fact]
    public async Task ProductionStartupLogsAndContinuesWhenSqliteAppSchemaIsMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-aspnetcore-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var modelPath = Path.Combine(directory, "model.json");
        File.WriteAllText(modelPath, JsonSerializer.Serialize(CreateModel(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = directory,
            EnvironmentName = Environments.Production
        });
        builder.Services.AddBever(options =>
        {
            options.ModelPath = "model.json";
            options.DatabasePath = NewDatabasePath();
        });
        await using var app = builder.Build();

        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
    }

    [Fact]
    public void ExplicitProviderOverridesSqliteDefault()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IBeverDatabaseProvider, InMemoryBeverDatabaseProvider>();
        builder.Services.AddBever();

        Assert.IsType<InMemoryBeverDatabaseProvider>(builder.Services.BuildServiceProvider().GetRequiredService<IBeverDatabaseProvider>());
    }

    [Fact]
    public void ExplicitProviderKeepsInMemoryIdentityDefault()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IBeverDatabaseProvider, InMemoryBeverDatabaseProvider>();
        builder.Services.AddBever();

        Assert.IsType<InMemoryBeverIdentityStore>(builder.Services.BuildServiceProvider().GetRequiredService<IBeverIdentityStore>());
    }

    [Fact]
    public async Task ProductionStartupFailsClosedForIdentityWithoutSqliteStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-aspnetcore-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "model.json"), JsonSerializer.Serialize(CreateIdentityModel(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = directory,
            EnvironmentName = Environments.Production
        });
        builder.Services.AddSingleton<IBeverDatabaseProvider, InMemoryBeverDatabaseProvider>();
        builder.Services.AddBever(options => options.ModelPath = "model.json");
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Contains("SQLite-backed identity store", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddBeverUsesSqliteIdentityStoreForSqliteProvider()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-aspnetcore-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var modelPath = Path.Combine(directory, "model.json");
        var databasePath = Path.Combine(directory, ".bever", "bever.db");
        File.WriteAllText(modelPath, JsonSerializer.Serialize(CreateIdentityModel(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = directory,
            EnvironmentName = Environments.Development
        });
        builder.Services.AddBever(options =>
        {
            options.ModelPath = "model.json";
            options.DatabasePath = databasePath;
        });
        builder.Services.AddSingleton<IAntiforgery, TestAntiforgery>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.MapBeverApi();

        Assert.IsNotType<InMemoryBeverIdentityStore>(app.Services.GetRequiredService<IBeverIdentityStore>());
        var database = app.Services.GetRequiredService<Bever.Sqlite.BeverSqliteDatabase>();
        await database.EnsureCreatedAsync();
        await database.ApplyIdentitySchemaAsync();
        await database.RegisterIdentityAsync("ada@example.com", "correct horse battery staple", default);

        var address = await StartServerAsync(app);
        using var client = CreateClient(address);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/_identity/login", new { email = "ada@example.com", password = "correct horse battery staple" })).StatusCode);
        var session = await (await client.GetAsync("/api/v1/_identity/session")).Content.ReadFromJsonAsync<JsonObject>();

        Assert.True(session!["authenticated"]!.GetValue<bool>());
        Assert.Equal("ada@example.com", session["identity"]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task SqliteDuplicateRegistrationReturnsConflictUnderConcurrency()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-aspnetcore-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var modelPath = Path.Combine(directory, "model.json");
        var databasePath = Path.Combine(directory, ".bever", "bever.db");
        File.WriteAllText(modelPath, JsonSerializer.Serialize(CreateRegistrationModel(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = directory,
            EnvironmentName = Environments.Development
        });
        builder.Services.AddBever(options =>
        {
            options.ModelPath = "model.json";
            options.DatabasePath = databasePath;
        });
        builder.Services.AddSingleton<IAntiforgery, TestAntiforgery>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.MapBeverApi();

        var database = app.Services.GetRequiredService<Bever.Sqlite.BeverSqliteDatabase>();
        await database.EnsureCreatedAsync();
        await database.ApplyIdentitySchemaAsync();
        var address = await StartServerAsync(app);
        using var client = CreateClient(address);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
        {
            var client = CreateClient(address);
            return client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "race@example.com", password = "correct horse battery staple" });
        }));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task SqliteLookupMatchesInMemoryForOrderingCursorsDeletedExclusionAndHydration()
    {
        var entity = new BeverEntityContext(CreateLookupEntity(), "products");
        var sqliteDatabase = new Bever.Sqlite.BeverSqliteDatabase(NewDatabasePath(), CreateLookupSchema());
        await sqliteDatabase.EnsureCreatedAsync();
        IBeverDatabaseProvider sqlite = new SqliteBeverDatabaseProvider(sqliteDatabase);
        IBeverDatabaseProvider memory = new InMemoryBeverDatabaseProvider();

        await AssertProviderBehaviorAsync(memory, entity);
        await AssertProviderBehaviorAsync(sqlite, entity);
    }

    [Fact]
    public async Task SqliteIdentityStoreIgnoresCrudDirectGrantAction()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-aspnetcore-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "model.json"), JsonSerializer.Serialize(CreateModel(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = directory,
            EnvironmentName = Environments.Development
        });
        builder.Services.AddBever(options =>
        {
            options.ModelPath = "model.json";
            options.DatabasePath = NewDatabasePath();
        });
        await using var app = builder.Build();
        var database = app.Services.GetRequiredService<Bever.Sqlite.BeverSqliteDatabase>();
        await database.EnsureCreatedAsync();
        await database.ApplyIdentitySchemaAsync();
        var identity = await database.RegisterIdentityAsync("grant@example.com", "correct horse battery staple", default);
        var store = app.Services.GetRequiredService<IBeverIdentityStore>();
        var entity = new BeverEntityContext(new EntityModel { Name = "Product" }, "products");

        await database.AddDirectGrantAsync(identity.Id, "Product", "Crud", null, "test", default);

        Assert.False(await store.HasDirectGrantAsync(identity.Id, entity, BeverEntityAction.Read, "record-1", default));

        await database.AddDirectGrantAsync(identity.Id, "Product", "Read", null, "test", default);
        Assert.True(await store.HasDirectGrantAsync(identity.Id, entity, BeverEntityAction.Read, "record-1", default));
    }

    [Fact]
    public async Task RestrictedDeleteReturnsAConflictThatNamesNoRecordIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-aspnetcore-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var text = new FieldTypeModel { Kind = "Primitive", Name = "Text" };
        var anyone = new List<AuthorizationModel> { new() { Kind = "Anyone" } };
        var model = new BeverModel
        {
            BeverLanguageVersion = BeverModel.LanguageVersion,
            SemanticModelVersion = BeverModel.ModelVersion,
            Entities =
            [
                new EntityModel { Name = "Customer", Fields = [new FieldModel { Name = "Name", Type = text }] },
                new EntityModel { Name = "Order", Fields = [new FieldModel { Name = "Number", Type = text }, new FieldModel { Name = "Customer", Type = new FieldTypeModel { Kind = "Relationship", Targets = ["Customer"], TargetKind = "Application", OnDelete = "Restrict" } }] }
            ],
            Actions = [.. new[] { "Create", "Read", "Delete" }.SelectMany(verb => new[] { new ActionModel { Verb = verb, Entity = "Customer", Authorizations = anyone }, new ActionModel { Verb = verb, Entity = "Order", Authorizations = anyone } })]
        };
        File.WriteAllText(Path.Combine(directory, "model.json"), JsonSerializer.Serialize(model, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory, EnvironmentName = Environments.Development });
        builder.Services.AddBever(options => options.ModelPath = "model.json");
        builder.Services.AddSingleton<IAntiforgery, TestAntiforgery>();
        await using var app = builder.Build();
        app.MapBeverApi();
        await app.Services.GetRequiredService<Bever.Sqlite.BeverSqliteDatabase>().EnsureCreatedAsync();
        using var client = CreateClient(await StartServerAsync(app));

        var customer = (await (await client.PostAsJsonAsync("/api/v1/customers", new { name = "Northwind" })).Content.ReadFromJsonAsync<JsonObject>())!;
        var id = customer["id"]!.GetValue<string>();
        (await client.PostAsJsonAsync("/api/v1/orders", new { number = "SO-1", customer = id })).EnsureSuccessStatusCode();

        var response = await client.DeleteAsync($"/api/v1/customers/{id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"DeleteRestricted\"", body);
        Assert.Contains("Order records still reference this record through Customer.", body);
        Assert.DoesNotContain(id, body);
    }

    private static BeverModel CreateModel() => new()
    {
        BeverLanguageVersion = "0.6",
        SemanticModelVersion = "0.10",
        Identity = IdentityModel.Disabled(),
        Entities =
        [
            new EntityModel
            {
                Name = "Product",
                Fields =
                [
                    new FieldModel
                    {
                        Name = "Name",
                        Type = new FieldTypeModel { Kind = "Primitive", Name = "Text" }
                    }
                ]
            }
        ]
    };

    private static BeverModel CreateIdentityModel() => new()
    {
        BeverLanguageVersion = "0.6",
        SemanticModelVersion = "0.10",
        Identity = new IdentityModel(true, ["Login", "Logout", "Session"], new IdentitySummaryModel("Email"), [new IdentitySystemTargetModel("Identity", "SystemIdentity", new IdentityLookupModel("IdLabel", true, false))])
    };

    private static BeverModel CreateRegistrationModel() => new()
    {
        BeverLanguageVersion = "0.6",
        SemanticModelVersion = "0.10",
        Identity = new IdentityModel(true, ["Login", "Logout", "Session", "Register"], new IdentitySummaryModel("Email"), [new IdentitySystemTargetModel("Identity", "SystemIdentity", new IdentityLookupModel("IdLabel", true, false))])
    };

    private static async Task AssertProviderBehaviorAsync(IBeverDatabaseProvider provider, BeverEntityContext entity)
    {
        var alpha = await provider.CreateAsync(entity, new JsonObject { ["name"] = "Alpha", ["active"] = true, ["score"] = 10, ["price"] = 12.34m, ["shipDate"] = "2026-09-10", ["shipAt"] = "2026-09-10T12:34:56+02:00", ["status"] = "Open", ["note"] = null }, default);
        var alphabet = await provider.CreateAsync(entity, new JsonObject { ["name"] = "alphabet", ["active"] = false, ["score"] = 2, ["price"] = 2.50m, ["shipDate"] = "2026-09-11", ["shipAt"] = "2026-09-11T12:34:56Z", ["status"] = "Closed", ["note"] = "x" }, default);
        var contains = await provider.CreateAsync(entity, new JsonObject { ["name"] = "The alpha", ["active"] = true, ["score"] = 30, ["price"] = 30m, ["shipDate"] = "2026-09-12", ["shipAt"] = "2026-09-12T12:34:56Z", ["status"] = "Open", ["note"] = "x" }, default);
        var deleted = await provider.CreateAsync(entity, new JsonObject { ["name"] = "Alpine", ["active"] = true, ["score"] = 40, ["price"] = 40m, ["shipDate"] = "2026-09-13", ["shipAt"] = "2026-09-13T12:34:56Z", ["status"] = "Open", ["note"] = "x" }, default);
        await provider.DeleteAsync(entity, deleted["id"]!.GetValue<string>(), null, default);

        var first = await provider.LookupAsync(new BeverLookupRequest(entity, "Name", ["Name"], "alp", 2, null), default);
        Assert.Equal(["Alpha", "alphabet"], first.Items.Select(item => item.Label).ToArray());
        Assert.NotNull(first.NextCursor);

        var normalized = await provider.LookupAsync(new BeverLookupRequest(entity, "Name", ["Name"], "alpha", 10, null), default);
        Assert.Equal(["Alpha", "alphabet", "The alpha"], normalized.Items.Select(item => item.Label).ToArray());

        var second = await provider.LookupAsync(new BeverLookupRequest(entity, "Name", ["Name"], "alp", 2, first.NextCursor), default);
        Assert.Equal(["The alpha"], second.Items.Select(item => item.Label).ToArray());
        Assert.Null(second.NextCursor);

        await Assert.ThrowsAsync<BeverLookupInvalidCursorException>(() =>
            provider.LookupAsync(new BeverLookupRequest(entity, "Name", ["Name"], "different", 2, first.NextCursor), default));
        await Assert.ThrowsAsync<BeverLookupInvalidCursorException>(() =>
            provider.LookupAsync(new BeverLookupRequest(entity, "Name", ["Name"], "alp", 3, first.NextCursor), default));
        await Assert.ThrowsAsync<BeverLookupInvalidCursorException>(() =>
            provider.LookupAsync(new BeverLookupRequest(entity, "Name", ["Name", "Active"], "alp", 2, first.NextCursor), default));
        await Assert.ThrowsAsync<BeverLookupInvalidCursorException>(() =>
            provider.LookupAsync(new BeverLookupRequest(new BeverEntityContext(entity.Model, "other-products"), "Name", ["Name"], "alp", 2, first.NextCursor), default));
        await Assert.ThrowsAsync<BeverLookupInvalidCursorException>(() =>
            provider.LookupAsync(new BeverLookupRequest(entity, "Name", ["Name"], "alp", 2, "not+base64url"), default));

        var hydrated = await provider.HydrateLookupAsync(new BeverLookupHydrationRequest(entity, "Score", [
            contains["id"]!.GetValue<string>(),
            alpha["id"]!.GetValue<string>(),
            contains["id"]!.GetValue<string>(),
            deleted["id"]!.GetValue<string>(),
            "missing"
        ]), default);
        Assert.Equal(["30", "10"], hydrated.Select(item => item.Label).ToArray());

        var booleanLabel = await provider.HydrateLookupAsync(new BeverLookupHydrationRequest(entity, "Active", [alphabet["id"]!.GetValue<string>()]), default);
        Assert.Equal("false", Assert.Single(booleanLabel).Label);

        Assert.Equal("12.34", Assert.Single(await provider.HydrateLookupAsync(new BeverLookupHydrationRequest(entity, "Price", [alpha["id"]!.GetValue<string>()]), default)).Label);
        Assert.Equal("2026-09-10", Assert.Single(await provider.HydrateLookupAsync(new BeverLookupHydrationRequest(entity, "ShipDate", [alpha["id"]!.GetValue<string>()]), default)).Label);
        Assert.Equal("2026-09-10T10:34:56Z", Assert.Single(await provider.HydrateLookupAsync(new BeverLookupHydrationRequest(entity, "ShipAt", [alpha["id"]!.GetValue<string>()]), default)).Label);
        Assert.Equal("Open", Assert.Single(await provider.HydrateLookupAsync(new BeverLookupHydrationRequest(entity, "Status", [alpha["id"]!.GetValue<string>()]), default)).Label);
        Assert.Equal("", Assert.Single(await provider.HydrateLookupAsync(new BeverLookupHydrationRequest(entity, "Note", [alpha["id"]!.GetValue<string>()]), default)).Label);
        Assert.Equal("", Assert.Single(await provider.HydrateLookupAsync(new BeverLookupHydrationRequest(entity, "", [alpha["id"]!.GetValue<string>()]), default)).Label);

        // A row scope keeps rows whose owner field holds the actor, plus granted IDs; deleted rows stay excluded.
        var scope = new BeverRowScope("x", ["Note"], [alpha["id"]!.GetValue<string>()]);
        var scoped = await provider.LookupAsync(new BeverLookupRequest(entity, "Name", ["Name"], "", 10, null, scope), default);
        Assert.Equal(["Alpha", "alphabet", "The alpha"], scoped.Items.Select(item => item.Label));
        var owned = await provider.HydrateLookupAsync(new BeverLookupHydrationRequest(entity, "Name", [alpha["id"]!.GetValue<string>(), contains["id"]!.GetValue<string>()], new BeverRowScope("x", ["Note"], [])), default);
        Assert.Equal(["The alpha"], owned.Select(item => item.Label));
    }

    private static EntityModel CreateLookupEntity() => new()
    {
        Name = "Product",
        Fields =
        [
            new FieldModel { Name = "Name", Type = new FieldTypeModel { Kind = "Primitive", Name = "Text" } },
            new FieldModel { Name = "Active", Type = new FieldTypeModel { Kind = "Primitive", Name = "Boolean" } },
            new FieldModel { Name = "Score", Type = new FieldTypeModel { Kind = "Primitive", Name = "Int" } },
            new FieldModel { Name = "Price", Type = new FieldTypeModel { Kind = "Primitive", Name = "Decimal" } },
            new FieldModel { Name = "ShipDate", Type = new FieldTypeModel { Kind = "Primitive", Name = "Date" } },
            new FieldModel { Name = "ShipAt", Type = new FieldTypeModel { Kind = "Primitive", Name = "DateTime" } },
            new FieldModel { Name = "Status", Type = new FieldTypeModel { Kind = "Enum", Options = [new EnumOptionModel("Open", "open"), new EnumOptionModel("Closed", "closed")] } },
            new FieldModel { Name = "Note", Type = new FieldTypeModel { Kind = "Primitive", Name = "Text", Nullable = true } }
        ]
    };

    private static PersistenceSchema CreateLookupSchema() => new()
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
                    new PersistenceField { Name = "Name", Kind = "Primitive", TypeName = "Text", Nullable = false },
                    new PersistenceField { Name = "Active", Kind = "Primitive", TypeName = "Boolean", Nullable = false },
                    new PersistenceField { Name = "Score", Kind = "Primitive", TypeName = "Int", Nullable = false },
                    new PersistenceField { Name = "Price", Kind = "Primitive", TypeName = "Decimal", Nullable = false },
                    new PersistenceField { Name = "ShipDate", Kind = "Primitive", TypeName = "Date", Nullable = false },
                    new PersistenceField { Name = "ShipAt", Kind = "Primitive", TypeName = "DateTime", Nullable = false },
                    new PersistenceField { Name = "Status", Kind = "Enum", TypeName = "Status", Nullable = false, EnumValues = ["Open", "Closed"] },
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

    private static string NewDatabasePath() => Path.Combine(
        Path.GetTempPath(),
        "bever-aspnetcore-sqlite-tests",
        Guid.NewGuid().ToString("N"),
        "bever.db");

    private static async Task<string> StartServerAsync(WebApplication app)
    {
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        return app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    private static HttpClient CreateClient(string address)
    {
        var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add(TestAntiforgery.HeaderName, TestAntiforgery.Token);
        return client;
    }

    private sealed class TestAntiforgery : IAntiforgery
    {
        public const string HeaderName = "X-CSRF-TOKEN";
        public const string Token = "test-csrf-token";

        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => GetTokens(httpContext);

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => new(Token, Token, "__RequestVerificationToken", HeaderName);

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) =>
            Task.FromResult(httpContext.Request.Headers.TryGetValue(HeaderName, out var value) && value == Token);

        public void SetCookieTokenAndHeader(HttpContext httpContext)
        {
        }

        public Task ValidateRequestAsync(HttpContext httpContext) =>
            httpContext.Request.Headers.TryGetValue(HeaderName, out var value) && value == Token
                ? Task.CompletedTask
                : throw new AntiforgeryValidationException("missing test antiforgery token");
    }
}
