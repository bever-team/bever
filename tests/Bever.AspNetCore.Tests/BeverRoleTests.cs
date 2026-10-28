using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bever.Model;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bever.AspNetCore.Tests;

// Role rules, the session's roles, and the Administrator-only account routes, end to end over HTTP.
public sealed class BeverRoleTests
{
    private static readonly IdentityModel Identity = new(true, ["Login", "Logout", "Session", "Register"], new IdentitySummaryModel("Email"), [new IdentitySystemTargetModel("Identity", "SystemIdentity", new IdentityLookupModel("IdLabel", true, false))]);

    // Agents read every ticket, others only their own; only Managers delete.
    private static readonly BeverModel Model = new()
    {
        BeverLanguageVersion = "0.6",
        SemanticModelVersion = "0.10",
        Identity = Identity,
        Roles = [new RoleModel { Name = "Agent" }, new RoleModel { Name = "Manager" }],
        Entities =
        [
            new EntityModel
            {
                Name = "Ticket",
                Fields =
                [
                    new FieldModel { Name = "Subject", Type = new FieldTypeModel { Kind = "Primitive", Name = "Text" } },
                    new FieldModel { Name = "Owner", Type = new FieldTypeModel { Kind = "Relationship", Targets = ["Identity"], OnDelete = "Restrict", TargetKind = "SystemIdentity" } }
                ]
            }
        ],
        Actions =
        [
            new ActionModel { Verb = "Create", Entity = "Ticket", Authorizations = [new AuthorizationModel("Authenticated", null)] },
            new ActionModel { Verb = "Read", Entity = "Ticket", Authorizations = [new AuthorizationModel("Role", null, "Agent"), new AuthorizationModel("Path", new ReferencePathModel(["Owner"]))] },
            new ActionModel { Verb = "Delete", Entity = "Ticket", Authorizations = [new AuthorizationModel("Role", null, "Manager")] }
        ]
    };

    [Fact]
    public async Task RoleRulesAllowMembersAndRevocationAppliesAtOnce()
    {
        var (app, store) = await StartAsync(Model);
        await using var _ = app;
        var (ada, adaId) = await RegisterAsync(app, "ada@example.com");
        var (bob, bobId) = await RegisterAsync(app, "bob@example.com");
        await CreateTicketAsync(ada, "Printer", adaId);
        var bobs = await CreateTicketAsync(bob, "Laptop", bobId);

        Assert.Equal(["Printer"], await SubjectsAsync(ada));
        store.AddRole(adaId, "agent");
        Assert.Equal(["Laptop", "Printer"], await SubjectsAsync(ada));
        Assert.Equal(["Laptop"], await SubjectsAsync(bob));

        Assert.Equal(HttpStatusCode.Forbidden, (await ada.DeleteAsync($"/api/v1/tickets/{bobs}")).StatusCode);
        await store.SetRolesAsync(adaId, ["Agent", "Manager"], adaId, default);
        Assert.Equal(HttpStatusCode.NoContent, (await ada.DeleteAsync($"/api/v1/tickets/{bobs}")).StatusCode);

        await store.SetRolesAsync(adaId, [], adaId, default);
        Assert.Equal(["Printer"], await SubjectsAsync(ada));
        using var anonymous = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/tickets")).StatusCode);
    }

    [Fact]
    public async Task SessionListsTheCallersDeclaredRoles()
    {
        var (app, store) = await StartAsync(Model);
        await using var _ = app;
        var (ada, adaId) = await RegisterAsync(app, "ada@example.com");
        store.AddRole(adaId, "Manager");
        store.AddRole(adaId, "Retired");

        var session = await ada.GetFromJsonAsync<JsonObject>("/api/v1/_identity/session");
        using var anonymous = Client(app);
        var signedOut = await anonymous.GetFromJsonAsync<JsonObject>("/api/v1/_identity/session");

        Assert.Equal(["authenticated", "identity", "administrator", "roles"], session!.Select(property => property.Key));
        Assert.Equal(["Manager"], session!["roles"]!.AsArray().Select(role => role!.GetValue<string>()));
        Assert.Empty(signedOut!["roles"]!.AsArray());
    }

    [Fact]
    public async Task AdministratorsListAccountsAndSetTheirRoles()
    {
        var (app, store) = await StartAsync(Model);
        await using var _ = app;
        var (ada, adaId) = await RegisterAsync(app, "ada@example.com");
        var (bob, bobId) = await RegisterAsync(app, "bob@example.com");
        store.AddAdministrator(adaId);

        var accounts = await ada.GetFromJsonAsync<JsonObject>("/api/v1/_identity/accounts?search=BOB");
        var item = Assert.Single(accounts!["items"]!.AsArray())!.AsObject();
        Assert.Equal(["id", "label", "administrator", "roles"], item.Select(property => property.Key));
        Assert.Equal((bobId, "bob@example.com", false), (item["id"]!.GetValue<string>(), item["label"]!.GetValue<string>(), item["administrator"]!.GetValue<bool>()));

        var updated = await ada.PatchAsJsonAsync($"/api/v1/_identity/accounts/{bobId}", new { roles = new[] { "Manager", "Agent" } });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(["Agent", "Manager"], (await updated.Content.ReadFromJsonAsync<JsonObject>())!["roles"]!.AsArray().Select(role => role!.GetValue<string>()));
        Assert.Equal(["Agent", "Manager"], (await store.ListRolesAsync(bobId, default)).Order(StringComparer.Ordinal));

        var page = await ada.GetFromJsonAsync<JsonObject>("/api/v1/_identity/accounts?limit=1");
        Assert.Equal("ada@example.com", Assert.Single(page!["items"]!.AsArray())!["label"]!.GetValue<string>());
        Assert.True(page["items"]![0]!["administrator"]!.GetValue<bool>());
        var next = await ada.GetFromJsonAsync<JsonObject>($"/api/v1/_identity/accounts?limit=1&cursor={page["nextCursor"]!.GetValue<string>()}");
        Assert.Equal("bob@example.com", Assert.Single(next!["items"]!.AsArray())!["label"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/v1/_identity/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PatchAsJsonAsync($"/api/v1/_identity/accounts/{bobId}", new { roles = Array.Empty<string>() })).StatusCode);
        using var anonymous = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/_identity/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ada.PatchAsJsonAsync("/api/v1/_identity/accounts/idn_missing", new { roles = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ada.GetAsync("/api/v1/_identity/accounts?picker=Mine")).StatusCode);
    }

    [Theory]
    [InlineData("""{ "roles": ["Auditor"] }""")]
    [InlineData("""{ "roles": ["agent"] }""")]
    [InlineData("""{ "roles": ["Agent", "Agent"] }""")]
    [InlineData("""{ "roles": "Agent" }""")]
    [InlineData("""{ "roles": [], "administrator": true }""")]
    [InlineData("""[]""")]
    public async Task SettingRolesNeedsDeclaredRoleNames(string body)
    {
        var (app, store) = await StartAsync(Model);
        await using var _ = app;
        var (ada, adaId) = await RegisterAsync(app, "ada@example.com");
        store.AddAdministrator(adaId);

        var response = await ada.PatchAsync($"/api/v1/_identity/accounts/{adaId}", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await store.ListRolesAsync(adaId, default));
    }

    [Fact]
    public async Task AccountRoutesExistOnlyWhenTheModelDeclaresRoles()
    {
        var withoutRoles = new BeverModel { BeverLanguageVersion = "0.6", SemanticModelVersion = "0.10", Identity = Identity };
        var (app, store) = await StartAsync(withoutRoles);
        await using var _ = app;
        var (ada, adaId) = await RegisterAsync(app, "ada@example.com");
        store.AddAdministrator(adaId);

        Assert.Equal(HttpStatusCode.NotFound, (await ada.GetAsync("/api/v1/_identity/accounts")).StatusCode);
    }

    private static async Task<(WebApplication App, InMemoryBeverIdentityStore Store)> StartAsync(BeverModel model)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-role-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "model.json"), JsonSerializer.Serialize(model, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var store = new InMemoryBeverIdentityStore();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory, EnvironmentName = Environments.Development });
        builder.Services.AddSingleton<IBeverDatabaseProvider, InMemoryBeverDatabaseProvider>();
        builder.Services.AddSingleton<IBeverIdentityStore>(store);
        builder.Services.AddBever(options => options.ModelPath = "model.json");
        builder.Services.AddSingleton<IAntiforgery, AllowAntiforgery>();
        var app = builder.Build();
        app.UseAuthentication();
        app.MapBeverApi();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        return (app, store);
    }

    private static HttpClient Client(WebApplication app) =>
        new() { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };

    private static async Task<(HttpClient Client, string IdentityId)> RegisterAsync(WebApplication app, string email)
    {
        var client = Client(app);
        var registered = await client.PostAsJsonAsync("/api/v1/_identity/register", new { email, password = "correct horse battery staple" });
        return (client, (await registered.Content.ReadFromJsonAsync<JsonObject>())!["identity"]!["id"]!.GetValue<string>());
    }

    private static async Task<string> CreateTicketAsync(HttpClient client, string subject, string owner)
    {
        var created = await client.PostAsJsonAsync("/api/v1/tickets", new { subject, owner });
        return (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
    }

    private static async Task<string[]> SubjectsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonArray>("/api/v1/tickets"))!.Select(item => item!["subject"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();

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
