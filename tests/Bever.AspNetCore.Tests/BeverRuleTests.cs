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

// Entity rules, end to end over HTTP: checked on the resulting record, empty values pass, errors name the first field.
public sealed class BeverRuleTests
{
    private const string Token = "test-csrf-token";

    [Theory]
    [InlineData("amount", 0, "amount", "Amount must be positive")]
    [InlineData("status", "Rejected", "status", "Give a reason for a rejection")]
    [InlineData("minimum", 5, "minimum", "Minimum cannot exceed maximum")]
    [InlineData("due", "2000-01-01", "due", "Due date cannot be in the past")]
    public async Task CreateRefusesRecordsThatBreakARule(string field, object value, string errorField, string message)
    {
        using var app = CreateApp();
        using var client = await StartAsync(app);
        var body = Valid();
        body[field] = JsonValue.Create(value);

        var response = await client.PostAsJsonAsync("/api/v1/refunds", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = Assert.Single((await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsArray());
        Assert.Equal(errorField, error!["field"]!.GetValue<string>());
        Assert.Equal(message, error["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task EmptyValuesPassComparisons()
    {
        using var app = CreateApp();
        using var client = await StartAsync(app);

        var response = await client.PostAsJsonAsync("/api/v1/refunds", new JsonObject { ["amount"] = 10, ["status"] = "Draft", ["minimum"] = 5 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UpdateChecksTheRecordAsItWillBe()
    {
        using var app = CreateApp();
        using var client = await StartAsync(app);
        var created = await (await client.PostAsJsonAsync("/api/v1/refunds", Valid())).Content.ReadFromJsonAsync<JsonObject>();
        var route = $"/api/v1/refunds/{created!["id"]!.GetValue<string>()}";

        var rejected = await client.PatchAsJsonAsync(route, new JsonObject { ["status"] = "Rejected" });
        var tooLow = await client.PatchAsJsonAsync(route, new JsonObject { ["maximum"] = 0 });
        (await client.PatchAsJsonAsync(route, new JsonObject { ["reason"] = "Damaged" })).EnsureSuccessStatusCode();
        var rejectedWithReason = await client.PatchAsJsonAsync(route, new JsonObject { ["status"] = "Rejected" });

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLow.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rejectedWithReason.StatusCode);
    }

    private static JsonObject Valid() => new()
    {
        ["amount"] = 25.5,
        ["status"] = "Draft",
        ["minimum"] = 1,
        ["maximum"] = 3,
        ["due"] = "2999-01-01"
    };

    private static WebApplication CreateApp()
    {
        static FieldModel Field(string name, string type, bool nullable = false) => new() { Name = name, Type = new FieldTypeModel { Kind = "Primitive", Name = type, Nullable = nullable } };
        static ExpressionModel Ref(string name) => new("Field", path: new ReferencePathModel([name]));
        static ExpressionModel Literal(string kind, object value) => new("Literal", literal: new LiteralValueModel(kind, value));
        static ExpressionModel Compare(ExpressionModel left, string op, ExpressionModel right) => new("Compare", op, [left, right]);
        static RuleModel Rule(ExpressionModel condition, string message) => new() { Condition = condition, Message = message };
        var anyone = new List<AuthorizationModel> { new() { Kind = "Anyone" } };
        var model = new BeverModel
        {
            BeverLanguageVersion = BeverModel.LanguageVersion,
            SemanticModelVersion = BeverModel.ModelVersion,
            Entities =
            [
                new EntityModel
                {
                    Name = "Refund",
                    Fields =
                    [
                        Field("Amount", "Money"), Field("Reason", "Text", nullable: true), Field("Minimum", "Int", nullable: true), Field("Maximum", "Int", nullable: true), Field("Due", "Date", nullable: true),
                        new FieldModel { Name = "Status", Type = new FieldTypeModel { Kind = "Enum", Options = [new EnumOptionModel("Draft", null), new EnumOptionModel("Approved", null), new EnumOptionModel("Rejected", null)] } }
                    ],
                    Rules =
                    [
                        Rule(Compare(Ref("Amount"), ">", Literal("Integer", 0)), "Amount must be positive"),
                        Rule(new ExpressionModel("Or", operands: [new ExpressionModel("In", operands: [Ref("Status"), Literal("Identifier", "Draft"), Literal("Identifier", "Approved")]), Compare(Ref("Reason"), "!=", new ExpressionModel("Null"))]), "Give a reason for a rejection"),
                        Rule(Compare(Ref("Minimum"), "<=", Ref("Maximum")), "Minimum cannot exceed maximum"),
                        Rule(new ExpressionModel("Not", operands: [Compare(Ref("Due"), "<", new ExpressionModel("Today"))]), "Due date cannot be in the past")
                    ]
                }
            ],
            Actions =
            [
                new ActionModel { Verb = "Create", Entity = "Refund", Authorizations = anyone },
                new ActionModel { Verb = "Update", Entity = "Refund", Authorizations = anyone }
            ]
        };

        var directory = Path.Combine(Path.GetTempPath(), "bever-rule-tests", Guid.NewGuid().ToString("N"));
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
