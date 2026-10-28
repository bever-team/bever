using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bever.AspNetCore;
using Bever.Model;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bever.AspNetCore.Tests;

public sealed class BeverPickerTests
{
    // Customer: open only your own, pick any. Project: open any, pick only the ones you manage.
    // Secret: no Pick, so Read (own rows only) decides picking and labels.
    private static readonly BeverModel Model = new()
    {
        BeverLanguageVersion = "0.6",
        SemanticModelVersion = "0.10",
        Identity = new IdentityModel(true, ["Login", "Logout", "Session", "Register"], new IdentitySummaryModel("Email"), [new IdentitySystemTargetModel("Identity", "SystemIdentity", new IdentityLookupModel("IdLabel", true, false))]),
        Entities =
        [
            Entity("Customer", Text("Name"), Reference("Owner", "Identity")),
            Entity("Project", Text("Name"), Reference("Manager", "Identity")),
            Entity("Secret", Text("Name"), Reference("Owner", "Identity")),
            Entity("Task", Text("Title"), Reference("Project", "Project", picker: "Managed"), Reference("Customer", "Customer"), Reference("Secret", "Secret"), Reference("Assignee", "Identity"))
        ],
        Actions =
        [
            Action("Read", "Customer", "/Owner"),
            Action("Pick", "Customer", "Authenticated"),
            Action("Read", "Project", "Authenticated"),
            Action("Pick", "Project", "/Manager", name: "Managed"),
            Action("Read", "Secret", "/Owner"),
            Action("Create", "Task", "Authenticated"),
            Action("Read", "Task", "Authenticated"),
            Action("Update", "Task", "Authenticated")
        ]
    };

    [Fact]
    public async Task PickersSeparateSeeingFromPickingAndSavesRecheckThePicker()
    {
        using var app = await StartAsync();
        var (ada, adaId) = await RegisterAsync(app, "ada@example.com");
        var (bob, bobId) = await RegisterAsync(app, "bob@example.com");
        var acme = await SeedAsync(app, "Customer", "Acme", ("owner", bobId));
        await SeedAsync(app, "Customer", "Mine", ("owner", adaId));
        var apollo = await SeedAsync(app, "Project", "Apollo", ("manager", bobId));
        var zeus = await SeedAsync(app, "Project", "Zeus", ("manager", adaId));
        var adaSecret = await SeedAsync(app, "Secret", "Ada secret", ("owner", adaId));

        // Path-scoped Read filters rows instead of answering 403; the Customer picker is wider than Read.
        Assert.Equal(["Mine"], await NamesAsync(ada, "/api/v1/customers", "name"));
        Assert.Equal(["Acme", "Mine"], await NamesAsync(ada, "/api/v1/customers/_lookup", "label"));
        // The named Project picker is narrower than Read; the default picker falls back to Read.
        Assert.Equal(["Apollo", "Zeus"], await NamesAsync(ada, "/api/v1/projects/_lookup", "label"));
        Assert.Equal(["Zeus"], await NamesAsync(ada, "/api/v1/projects/_lookup?picker=managed", "label"));
        Assert.Equal(HttpStatusCode.BadRequest, (await ada.GetAsync("/api/v1/projects/_lookup?picker=Missing")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ada.GetAsync("/api/v1/secrets/_lookup?picker=Managed")).StatusCode);

        // Saves accept only what the field's picker offers, with one message for every rejection.
        foreach (var (field, value) in new[] { ("project", apollo), ("customer", "missing"), ("secret", "missing"), ("assignee", "nobody") })
        {
            var rejected = await ada.PostAsJsonAsync("/api/v1/tasks", new Dictionary<string, string> { ["title"] = "Rejected", ["project"] = zeus, [field] = value });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var error = Assert.Single((await rejected.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsArray());
            Assert.Equal((field, "The selected record is unavailable."), (error!["field"]!.GetValue<string>(), error["message"]!.GetValue<string>()));
        }

        var created = await ada.PostAsJsonAsync("/api/v1/tasks", new { title = "Launch", project = zeus, customer = acme, secret = adaSecret, assignee = adaId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var task = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("""{"project":"Zeus","customer":"Acme","secret":"Ada secret","assignee":"ada@example.com"}""", task["_labels"]!.ToJsonString());

        // Bob may read the task but not Ada secret: its label is withheld, never replaced by the ID.
        var asBob = (await bob.GetFromJsonAsync<JsonArray>("/api/v1/tasks"))!.Single()!;
        Assert.Equal("""{"project":"Zeus","customer":"Acme","secret":null,"assignee":"ada@example.com"}""", asBob["_labels"]!.ToJsonString());

        // An unchanged value is not re-checked: Bob cannot pick Zeus or Ada secret, but may still edit the task.
        var taskId = task["id"]!.GetValue<string>();
        var unchanged = await bob.PatchAsJsonAsync($"/api/v1/tasks/{taskId}", new { title = "Renamed", project = zeus, secret = adaSecret });
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        var moved = await bob.PatchAsJsonAsync($"/api/v1/tasks/{taskId}", new { project = apollo });
        Assert.Equal("Apollo", (await moved.Content.ReadFromJsonAsync<JsonObject>())!["_labels"]!["project"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PatchAsJsonAsync($"/api/v1/tasks/{taskId}", new { project = zeus })).StatusCode);
    }

    [Fact]
    public async Task HydrationShowsLabelsToReadersAndPickersOnly()
    {
        using var app = await StartAsync();
        var (ada, _) = await RegisterAsync(app, "ada@example.com");
        var (_, bobId) = await RegisterAsync(app, "bob@example.com");
        var bobSecret = await SeedAsync(app, "Secret", "Bob secret", ("owner", bobId));
        var acme = await SeedAsync(app, "Customer", "Acme", ("owner", bobId));

        var customers = await ada.PostAsJsonAsync("/api/v1/customers/_lookup/hydrate", new { ids = new[] { acme } });
        Assert.Equal("Acme", (await customers.Content.ReadFromJsonAsync<JsonObject>())!["items"]![0]!["label"]!.GetValue<string>());
        var secrets = await ada.PostAsJsonAsync("/api/v1/secrets/_lookup/hydrate", new { ids = new[] { bobSecret } });
        Assert.Empty((await secrets.Content.ReadFromJsonAsync<JsonObject>())!["items"]!.AsArray());
        using var anonymous = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/customers/_lookup")).StatusCode);
    }

    private static async Task<WebApplication> StartAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-picker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "model.json"), JsonSerializer.Serialize(Model, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory, EnvironmentName = Environments.Development });
        builder.Services.AddSingleton<IBeverDatabaseProvider, InMemoryBeverDatabaseProvider>();
        builder.Services.AddSingleton<IBeverIdentityStore>(new InMemoryBeverIdentityStore());
        builder.Services.AddBever(options => options.ModelPath = "model.json");
        builder.Services.AddSingleton<IAntiforgery, AllowAntiforgery>();
        var app = builder.Build();
        app.UseAuthentication();
        app.MapBeverApi();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) =>
        new() { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };

    private static async Task<(HttpClient Client, string IdentityId)> RegisterAsync(WebApplication app, string email)
    {
        var client = Client(app);
        var registered = await client.PostAsJsonAsync("/api/v1/_identity/register", new { email, password = "correct horse battery staple" });
        return (client, (await registered.Content.ReadFromJsonAsync<JsonObject>())!["identity"]!["id"]!.GetValue<string>());
    }

    private static async Task<string> SeedAsync(WebApplication app, string entityName, string name, (string Field, string Value) owner)
    {
        var entity = app.Services.GetRequiredService<BeverRuntime>().Entities.Single(entity => entity.Model.Name == entityName);
        var record = await app.Services.GetRequiredService<IBeverDatabaseProvider>().CreateAsync(entity, new JsonObject { ["name"] = name, [owner.Field] = owner.Value }, default);
        return record["id"]!.GetValue<string>();
    }

    private static async Task<string[]> NamesAsync(HttpClient client, string url, string property)
    {
        var json = (await client.GetFromJsonAsync<JsonNode>(url))!;
        var items = json is JsonArray array ? array : json["items"]!.AsArray();
        return items.Select(item => item![property]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
    }

    private static EntityModel Entity(string name, params FieldModel[] fields) => new() { Name = name, Kind = "Application", Fields = [.. fields] };

    private static FieldModel Text(string name) =>
        new() { Name = name, Type = new FieldTypeModel { Kind = "Primitive", Name = "Text", Arguments = [new Dictionary<string, object?> { ["maxLength"] = 100 }] } };

    private static FieldModel Reference(string name, string target, string? picker = null) =>
        new() { Name = name, Type = new FieldTypeModel { Kind = "Relationship", Nullable = true, Targets = [target], OnDelete = "SetNull", TargetKind = target == "Identity" ? "SystemIdentity" : "Application", Picker = picker } };

    private static ActionModel Action(string verb, string entity, string rule, string? name = null) =>
        new()
        {
            Verb = verb,
            Entity = entity,
            Name = name,
            Authorizations = [rule.StartsWith('/') ? new AuthorizationModel { Kind = "Path", Path = new ReferencePathModel([rule[1..]]) } : new AuthorizationModel { Kind = rule }]
        };

    private sealed class AllowAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => GetTokens(httpContext);

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => new("token", "token", "__RequestVerificationToken", "X-CSRF-TOKEN");

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);

        public void SetCookieTokenAndHeader(HttpContext httpContext)
        {
        }

        public Task ValidateRequestAsync(HttpContext httpContext) => Task.CompletedTask;
    }
}
