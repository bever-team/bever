using System.Net;
using System.Net.Http.Json;
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

// Typed value formats, required text, and list order, end to end over HTTP.
public sealed class BeverValueRulesTests
{
    private const string Token = "test-csrf-token";

    [Theory]
    [InlineData("email", "someone", "Enter a valid email address.")]
    [InlineData("website", "example.com", "Enter a full web address starting with http:// or https://.")]
    [InlineData("phone", "call me", "Enter a valid phone number.")]
    [InlineData("due", "31/01/2026", "Enter a date as YYYY-MM-DD.")]
    [InlineData("starts", "9am", "Enter a time as HH:MM.")]
    [InlineData("remindAt", "2026-01-31T09:00", "Enter a date and time with a time zone, for example 2026-01-31T09:00:00Z.")]
    [InlineData("title", "   ", "Field is required.")]
    public async Task RejectsValuesThatDoNotMatchTheirType(string field, string value, string message)
    {
        using var app = CreateApp(orderBy: null);
        using var client = await StartAsync(app);
        var body = Valid();
        body[field] = value;

        var response = await client.PostAsJsonAsync("/api/v1/contacts", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsArray();
        Assert.Contains(errors, error => error!["field"]!.GetValue<string>() == field && error["message"]!.GetValue<string>() == message);
    }

    [Fact]
    public async Task StoresDateTimesInUtc()
    {
        using var app = CreateApp(orderBy: null);
        using var client = await StartAsync(app);
        var body = Valid();
        body["remindAt"] = "2026-01-31T11:30:00+02:00";

        var response = await client.PostAsJsonAsync("/api/v1/contacts", body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("2026-01-31T09:30:00.000Z", created["remindAt"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, new[] { "Cleo", "Bram", "Anna" })]
    [InlineData("Asc", new[] { "Anna", "Bram", "Cleo" })]
    [InlineData("Desc", new[] { "Cleo", "Bram", "Anna" })]
    public async Task ListsFollowReadOrderByOrNewestFirst(string? direction, string[] expected)
    {
        using var app = CreateApp(direction is null ? null : new OrderByModel(new ReferencePathModel(["Title"]), direction));
        using var client = await StartAsync(app);
        foreach (var title in new[] { "Anna", "Bram", "Cleo" })
        {
            var body = Valid();
            body["title"] = title;
            (await client.PostAsJsonAsync("/api/v1/contacts", body)).EnsureSuccessStatusCode();
            await Task.Delay(5);
        }

        var records = (await client.GetFromJsonAsync<JsonArray>("/api/v1/contacts"))!;

        Assert.Equal(expected, records.Select(record => record!["title"]!.GetValue<string>()));
    }

    [Fact]
    public async Task EnumOrderFollowsDeclarationOrder()
    {
        using var app = CreateApp(new OrderByModel(new ReferencePathModel(["Level"]), "Desc"));
        using var client = await StartAsync(app);
        foreach (var level in new[] { "Low", "High", "Normal" })
        {
            var body = Valid();
            body["level"] = level;
            (await client.PostAsJsonAsync("/api/v1/contacts", body)).EnsureSuccessStatusCode();
        }

        var records = (await client.GetFromJsonAsync<JsonArray>("/api/v1/contacts"))!;

        Assert.Equal(["High", "Normal", "Low"], records.Select(record => record!["level"]!.GetValue<string>()));
    }

    private static JsonObject Valid() => new()
    {
        ["title"] = "Anna",
        ["email"] = "anna@example.com",
        ["website"] = "https://example.com",
        ["phone"] = "+31 (0)20 123 4567",
        ["due"] = "2026-01-31",
        ["starts"] = "09:30",
        ["remindAt"] = "2026-01-31T09:00:00Z",
        ["level"] = "Normal"
    };

    private static WebApplication CreateApp(OrderByModel? orderBy)
    {
        static FieldModel Field(string name, string type) => new() { Name = name, Type = new FieldTypeModel { Kind = "Primitive", Name = type } };
        var anyone = new List<AuthorizationModel> { new() { Kind = "Anyone" } };
        var model = new BeverModel
        {
            BeverLanguageVersion = BeverModel.LanguageVersion,
            SemanticModelVersion = BeverModel.ModelVersion,
            Entities =
            [
                new EntityModel
                {
                    Name = "Contact",
                    Fields = [Field("Title", "Text"), Field("Email", "Email"), Field("Website", "Url"), Field("Phone", "Phone"), Field("Due", "Date"), Field("Starts", "Time"), Field("RemindAt", "DateTime"),
                        new FieldModel { Name = "Level", Type = new FieldTypeModel { Kind = "Enum", Options = [new EnumOptionModel("Low", null), new EnumOptionModel("Normal", null), new EnumOptionModel("High", null)] } }]
                }
            ],
            Actions =
            [
                new ActionModel { Verb = "Create", Entity = "Contact", Authorizations = anyone },
                new ActionModel { Verb = "Read", Entity = "Contact", Authorizations = anyone, OrderBy = orderBy }
            ]
        };

        var directory = Path.Combine(Path.GetTempPath(), "bever-value-rules-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "model.json"), JsonSerializer.Serialize(model, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory, EnvironmentName = Environments.Development });
        builder.Services.AddSingleton<IBeverDatabaseProvider, InMemoryBeverDatabaseProvider>();
        builder.Services.AddBever(options => options.ModelPath = "model.json");
        builder.Services.AddSingleton<IAntiforgery, AcceptingAntiforgery>();
        var app = builder.Build();
        app.MapBeverApi();
        return app;
    }

    private static async Task<HttpClient> StartAsync(WebApplication app)
    {
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", Token);
        return client;
    }

    private sealed class AcceptingAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => GetTokens(httpContext);

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => new(Token, Token, "__RequestVerificationToken", "X-CSRF-TOKEN");

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);

        public void SetCookieTokenAndHeader(HttpContext httpContext)
        {
        }

        public Task ValidateRequestAsync(HttpContext httpContext) => Task.CompletedTask;
    }
}
