using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Bever.Model;

namespace Bever.AspNetCore.Tests;

public sealed class BeverEndpointRouteBuilderExtensionsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void MapsConcreteEntityEndpointsUnderDefaultApiPrefix()
    {
        using var app = CreateApp(
            new ActionModel { Verb = "Read", Entity = "SalesOrder" },
            new ActionModel { Verb = "Create", Entity = "SalesOrder" },
            new ActionModel { Verb = "Update", Entity = "SalesOrder" },
            new ActionModel { Verb = "Delete", Entity = "SalesOrder" });

        app.MapBeverApi();

        var endpoints = GetRouteEndpoints(app);
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders" && HasMethod(endpoint, "GET"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/_lookup" && HasMethod(endpoint, "GET"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/_lookup/hydrate" && HasMethod(endpoint, "POST"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders" && HasMethod(endpoint, "POST"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/{id}" && HasMethod(endpoint, "GET"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/{id}" && HasMethod(endpoint, "PATCH"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/{id}" && HasMethod(endpoint, "DELETE"));
        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText?.Contains("{entityRoute}", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void UsesConfiguredApiPrefix()
    {
        using var app = CreateApp([new ActionModel { Verb = "Read", Entity = "SalesOrder" }], options => options.ApiPrefix = "/business");

        app.MapBeverApi();

        var endpoints = GetRouteEndpoints(app);
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/business/sales-orders" && HasMethod(endpoint, "GET"));
    }

    [Fact]
    public void MapsOnlyReadEndpointsForReadAction()
    {
        using var app = CreateApp(new ActionModel { Verb = "Read", Entity = "SalesOrder" });

        app.MapBeverApi();

        var endpoints = GetRouteEndpoints(app);
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders" && HasMethod(endpoint, "GET"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/{id}" && HasMethod(endpoint, "GET"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/_lookup" && HasMethod(endpoint, "GET"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/_lookup/hydrate" && HasMethod(endpoint, "POST"));
        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders" && HasMethod(endpoint, "POST"));
        Assert.DoesNotContain(endpoints, endpoint => HasMethod(endpoint, "PATCH") || HasMethod(endpoint, "DELETE"));
    }

    [Fact]
    public void MapsOnlyCreateEndpointForCreateAction()
    {
        using var app = CreateApp(new ActionModel { Verb = "Create", Entity = "SalesOrder" });

        app.MapBeverApi();

        var endpoints = GetRouteEndpoints(app);
        Assert.Single(endpoints);
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders" && HasMethod(endpoint, "POST"));
    }

    [Fact]
    public void MapsOnlyPatchEndpointForUpdateAction()
    {
        using var app = CreateApp(new ActionModel { Verb = "Update", Entity = "SalesOrder" });

        app.MapBeverApi();

        var endpoints = GetRouteEndpoints(app);
        Assert.Single(endpoints);
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/{id}" && HasMethod(endpoint, "PATCH"));
    }

    [Fact]
    public void MapsOnlyDeleteEndpointForDeleteAction()
    {
        using var app = CreateApp(new ActionModel { Verb = "Delete", Entity = "SalesOrder" });

        app.MapBeverApi();

        var endpoints = GetRouteEndpoints(app);
        Assert.Single(endpoints);
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/{id}" && HasMethod(endpoint, "DELETE"));
    }

    [Fact]
    public void MapsNoEntityEndpointsWithoutActions()
    {
        using var app = CreateApp();

        app.MapBeverApi();

        Assert.Empty(GetRouteEndpoints(app));
    }

    [Fact]
    public void PresentationDoesNotChangeDeclaredEndpointSet()
    {
        var actions = new[]
        {
            new ActionModel { Verb = "Read", Entity = "SalesOrder" },
            new ActionModel { Verb = "Update", Entity = "SalesOrder" }
        };
        using var withoutPresentation = CreateApp(actions, null);
        using var withHiddenPresentation = CreateApp(actions, null, new EntityPresentationModel
        {
            Label = "Order",
            Listing = new PresentationSurfaceModel { Fields = [new ReferencePathModel(["Name"])] },
            ActionOverrides =
            [
                new ActionPresentationOverrideModel
                {
                    Action = new ActionReferenceModel("Update"),
                    Hidden = true
                }
            ]
        });

        withoutPresentation.MapBeverApi();
        withHiddenPresentation.MapBeverApi();

        Assert.Equal(EndpointSignatures(withoutPresentation), EndpointSignatures(withHiddenPresentation));
    }

    [Fact]
    public void DoesNotMapMetadataWithApiEndpoints()
    {
        using var app = CreateApp(new ActionModel { Verb = "Read", Entity = "SalesOrder" });

        app.MapBeverApi();

        var endpoints = GetRouteEndpoints(app);

        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText?.StartsWith("/_bever", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void MapsMetadataOnlyWhenExplicitlyRequested()
    {
        using var app = CreateApp(new ActionModel { Verb = "Read", Entity = "SalesOrder" });

        app.MapBeverMetadata();

        var endpoints = GetRouteEndpoints(app);

        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/_bever/model");
    }

    [Fact]
    public void DoesNotMapPutForEntityUpdates()
    {
        using var app = CreateApp();

        app.MapBeverApi();

        var endpoints = GetRouteEndpoints(app);
        Assert.DoesNotContain(endpoints, endpoint => HasMethod(endpoint, "PUT"));
    }

    [Fact]
    public async Task CreateFillsDefaultValuesBeforePersisting()
    {
        using var app = CreateApp(new ActionModel { Verb = "Create", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var response = await client.PostAsJsonAsync("/api/v1/sales-orders", new { name = "Widget" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var record = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(record);
        Assert.Equal("Widget", record!["name"]?.GetValue<string>());
        Assert.Equal(true, record["active"]?.GetValue<bool>());
    }

    [Fact]
    public async Task CreateRejectsMissingRequiredFields()
    {
        using var app = CreateApp(new ActionModel { Verb = "Create", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var response = await client.PostAsJsonAsync("/api/v1/sales-orders", new { active = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertValidationErrorAsync(response, "name", "Field is required.");
    }

    [Fact]
    public async Task CreateRejectsUnknownFields()
    {
        using var app = CreateApp(new ActionModel { Verb = "Create", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var response = await client.PostAsJsonAsync("/api/v1/sales-orders", new { name = "Widget", unknown = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertValidationErrorAsync(response, "unknown", "Field is not defined on this entity.");
    }

    [Fact]
    public async Task CreateRejectsInvalidPrimitiveTypes()
    {
        using var app = CreateApp(new ActionModel { Verb = "Create", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var response = await client.PostAsJsonAsync("/api/v1/sales-orders", new { name = 123 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertValidationErrorAsync(response, "name", "Expected text.");
    }

    [Fact]
    public async Task PatchRejectsNullForNonNullableFields()
    {
        using var app = CreateApp(
            new ActionModel { Verb = "Create", Entity = "SalesOrder" },
            new ActionModel { Verb = "Update", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);
        var created = await CreateSalesOrderAsync(client);

        var response = await client.PatchAsJsonAsync($"/api/v1/sales-orders/{created["id"]!.GetValue<string>()}", new { name = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertValidationErrorAsync(response, "name", "Field cannot be null.");
    }

    [Fact]
    public async Task PatchAllowsNullForNullableFields()
    {
        using var app = CreateApp(
            new ActionModel { Verb = "Create", Entity = "SalesOrder" },
            new ActionModel { Verb = "Update", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);
        var created = await CreateSalesOrderAsync(client, new { name = "Widget", note = "Initial" });

        var response = await client.PatchAsJsonAsync($"/api/v1/sales-orders/{created["id"]!.GetValue<string>()}", new { note = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(record);
        Assert.Null(record!["note"]);
    }

    [Theory]
    [InlineData("Update", "PATCH", HttpStatusCode.OK)]
    [InlineData("Delete", "DELETE", HttpStatusCode.NoContent)]
    public async Task MutationOnlyActionExecutesWithoutPublishingReadRoutes(string verb, string method, HttpStatusCode allowedStatus)
    {
        using var allowedApp = CreateApp([new ActionModel { Verb = verb, Entity = "SalesOrder" }], null);
        allowedApp.MapBeverApi();
        var runtime = allowedApp.Services.GetRequiredService<BeverRuntime>();
        var provider = allowedApp.Services.GetRequiredService<IBeverDatabaseProvider>();
        var seeded = await provider.CreateAsync(
            Assert.Single(runtime.Entities),
            new JsonObject { ["name"] = "Widget", ["active"] = true, ["note"] = null },
            CancellationToken.None);
        var id = seeded["id"]!.GetValue<string>();
        using var allowedClient = await CreateClientAsync(allowedApp);

        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/sales-orders/{id}")
        {
            Content = method == "PATCH" ? JsonContent.Create(new { name = "Changed" }) : null
        };
        var mutation = await allowedClient.SendAsync(request);
        var read = await allowedClient.GetAsync("/api/v1/sales-orders");

        Assert.Equal(allowedStatus, mutation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        if (method == "PATCH")
        {
            var updated = await mutation.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal("Changed", updated!["name"]!.GetValue<string>());
            Assert.Equal(id, updated["id"]!.GetValue<string>());
        }

        Assert.True(Enum.TryParse<BeverEntityAction>(verb, out var action));
        using var deniedApp = CreateApp(
            [new ActionModel { Verb = verb, Entity = "SalesOrder" }],
            null,
            addDefaultAnyoneAuthorization: false);
        deniedApp.MapBeverApi();
        using var deniedClient = await CreateClientAsync(deniedApp);
        using var deniedRequest = new HttpRequestMessage(new HttpMethod(method), "/api/v1/sales-orders/missing")
        {
            Content = method == "PATCH" ? JsonContent.Create(new { name = "Changed" }) : null
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await deniedClient.SendAsync(deniedRequest)).StatusCode);
    }

    [Fact]
    public async Task CrudSemanticsAreInvariantAcrossPresentationChanges()
    {
        var presentations = new EntityPresentationModel?[]
        {
            null,
            new() { Label = "Order" },
            new() { Label = "Changed order" },
            new()
            {
                ActionOverrides =
                [
                    new ActionPresentationOverrideModel
                    {
                        Action = new ActionReferenceModel("Create"),
                        Hidden = true
                    }
                ]
            },
            null
        };
        CrudSnapshot? baseline = null;

        foreach (var presentation in presentations)
        {
            using var app = CreateApp(
                [
                    new ActionModel { Verb = "Create", Entity = "SalesOrder" },
                    new ActionModel { Verb = "Read", Entity = "SalesOrder" },
                    new ActionModel { Verb = "Update", Entity = "SalesOrder" },
                    new ActionModel { Verb = "Delete", Entity = "SalesOrder" }
                ],
                null,
                presentation);
            app.MapBeverApi();
            using var client = await CreateClientAsync(app);
            var snapshot = await ExerciseCrudAsync(client);

            baseline ??= snapshot;
            Assert.Equal(baseline, snapshot);
        }
    }

    [Fact]
    public async Task CombinedMutationOnlyActionsHonorIndependentDenialsAndPublishNoReadRoute()
    {
        using var app = CreateApp(
            [
                new ActionModel { Verb = "Update", Entity = "SalesOrder" },
                new ActionModel { Verb = "Delete", Entity = "SalesOrder", Authorizations = [new AuthorizationModel { Kind = "Anyone" }] }
            ],
            null,
            addDefaultAnyoneAuthorization: false);
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var patch = await client.PatchAsJsonAsync("/api/v1/sales-orders/missing", new { name = "Changed" });
        var delete = await client.DeleteAsync("/api/v1/sales-orders/missing");
        var read = await client.GetAsync("/api/v1/sales-orders");

        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task DenyingReadDoesNotDenyOtherwiseAuthorizedMutation()
    {
        using var app = CreateApp(
            [
                new ActionModel { Verb = "Read", Entity = "SalesOrder" },
                new ActionModel { Verb = "Update", Entity = "SalesOrder", Authorizations = [new AuthorizationModel { Kind = "Anyone" }] }
            ],
            null,
            addDefaultAnyoneAuthorization: false);
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var read = await client.GetAsync("/api/v1/sales-orders");
        var patch = await client.PatchAsJsonAsync("/api/v1/sales-orders/missing", new { name = "Changed" });

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
    }

    [Fact]
    public async Task UpdateOnlyRejectsMalformedJsonWithoutInvokingProvider()
    {
        using var app = CreateApp(new ActionModel { Verb = "Update", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);
        using var content = new StringContent("{", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PatchAsync("/api/v1/sales-orders/missing", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LookupReturnsBoundedSummariesInDeterministicOrderWithoutFullRecords()
    {
        using var app = CreateApp(new ActionModel { Verb = "Read", Entity = "SalesOrder", SearchBy = [new ReferencePathModel(["Name"])] }, new ActionModel { Verb = "Create", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);
        await client.PostAsJsonAsync("/api/v1/sales-orders", new { name = "Beta", note = "secret" });
        await client.PostAsJsonAsync("/api/v1/sales-orders", new { name = "alphabet", note = "secret" });
        await client.PostAsJsonAsync("/api/v1/sales-orders", new { name = "Alpha", note = "secret" });

        var response = await client.GetAsync("/api/v1/sales-orders/_lookup?search=alp&limit=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["items", "nextCursor"], body.Select(property => property.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray());
        var items = body["items"]!.AsArray();
        Assert.Equal(["Alpha", "alphabet"], items.Select(item => item!["label"]!.GetValue<string>()).ToArray());
        Assert.All(items, item => Assert.False(item!.AsObject().ContainsKey("note")));
        Assert.Null(body["nextCursor"]);
    }

    [Fact]
    public async Task LookupRejectsUnknownQueryAndOutOfBoundsLimit()
    {
        using var app = CreateApp(new ActionModel { Verb = "Read", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/sales-orders/_lookup?extra=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/sales-orders/_lookup?limit=51")).StatusCode);
    }

    [Theory]
    [InlineData("search=a&search=b")]
    [InlineData("limit=1&limit=2")]
    [InlineData("cursor=a&cursor=b")]
    [InlineData("limit=0")]
    [InlineData("limit=abc")]
    public async Task LookupRejectsClosedOrInvalidQueryShapes(string query)
    {
        using var app = CreateApp(new ActionModel { Verb = "Read", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var response = await client.GetAsync($"/api/v1/sales-orders/_lookup?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LookupRejectsOverlongSearchOverlongCursorAndNonBase64UrlCursor()
    {
        using var app = CreateApp(new ActionModel { Verb = "Read", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);
        var overlongSearch = new string('a', 201);
        var overlongCursor = new string('a', 2049);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/sales-orders/_lookup?search={overlongSearch}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/sales-orders/_lookup?cursor={overlongCursor}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/sales-orders/_lookup?cursor=not+base64url")).StatusCode);
    }

    [Fact]
    public async Task LookupNormalizesSearchAndSendsEffectiveSearchFieldsToProvider()
    {
        var database = new CapturingLookupProvider();
        using var app = CreateApp(
            [new ActionModel { Verb = "Read", Entity = "SalesOrder", SearchBy = [new ReferencePathModel(["Name"]), new ReferencePathModel(["Note"])] }],
            null,
            null,
            database);
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var response = await client.GetAsync("/api/v1/sales-orders/_lookup?search=%20ALPHA%09%20Beta%20&limit=7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(database.LastLookup);
        Assert.Equal("alpha beta", database.LastLookup!.NormalizedSearch);
        Assert.Equal(["Name", "Note"], database.LastLookup.SearchFields);
        Assert.Equal(7, database.LastLookup.Limit);
    }

    [Fact]
    public async Task LookupRoutesAreNotExposedWithoutTargetRead()
    {
        var database = new ThrowingLookupProvider();
        using var app = CreateApp([new ActionModel { Verb = "Update", Entity = "SalesOrder" }], null, null, database);
        app.MapBeverApi();
        var endpoints = GetRouteEndpoints(app);
        using var client = await CreateClientAsync(app);

        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/_lookup");
        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/sales-orders/_lookup/hydrate");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/v1/sales-orders/_lookup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/sales-orders/_lookup/hydrate", new { ids = new[] { "a" } })).StatusCode);
        Assert.Equal(0, database.LookupCalls);
    }

    [Fact]
    public async Task HydrateCoalescesDuplicatesPreservesFirstOrderAndRejectsMalformedBodies()
    {
        using var app = CreateApp(new ActionModel { Verb = "Read", Entity = "SalesOrder" }, new ActionModel { Verb = "Create", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);
        var beta = await CreateSalesOrderAsync(client, new { name = "Beta" });
        var alpha = await CreateSalesOrderAsync(client, new { name = "Alpha" });
        var betaId = beta["id"]!.GetValue<string>();
        var alphaId = alpha["id"]!.GetValue<string>();

        var response = await client.PostAsJsonAsync("/api/v1/sales-orders/_lookup/hydrate", new { ids = new[] { betaId, alphaId, betaId, "missing" } });
        var malformed = await client.PostAsJsonAsync("/api/v1/sales-orders/_lookup/hydrate", new { ids = Array.Empty<string>(), extra = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["items"], body.Select(property => property.Key).ToArray());
        var items = body["items"]!.AsArray();
        Assert.Equal([betaId, alphaId], items.Select(item => item!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task HydrateOmitsDeletedAndMissingRecordsWithoutFullRecords()
    {
        using var app = CreateApp(
            new ActionModel { Verb = "Read", Entity = "SalesOrder" },
            new ActionModel { Verb = "Create", Entity = "SalesOrder" },
            new ActionModel { Verb = "Delete", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);
        var kept = await CreateSalesOrderAsync(client, new { name = "Kept", note = "secret" });
        var deleted = await CreateSalesOrderAsync(client, new { name = "Deleted", note = "secret" });
        await client.DeleteAsync($"/api/v1/sales-orders/{deleted["id"]!.GetValue<string>()}");

        var response = await client.PostAsJsonAsync("/api/v1/sales-orders/_lookup/hydrate", new { ids = new[] { deleted["id"]!.GetValue<string>(), "missing", kept["id"]!.GetValue<string>() } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal([kept["id"]!.GetValue<string>()], body["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToArray());
        Assert.All(body["items"]!.AsArray(), item => Assert.False(item!.AsObject().ContainsKey("note")));
    }

    [Theory]
    [InlineData("text/plain", "{\"ids\":[\"a\"]}")]
    [InlineData("application/json", "{")]
    [InlineData("application/json", "[]")]
    [InlineData("application/json", "{\"ids\":\"a\"}")]
    [InlineData("application/json", "{\"ids\":[]}")]
    [InlineData("application/json", "{\"ids\":[\"\"]}")]
    [InlineData("application/json", "{\"ids\":[1]}")]
    [InlineData("application/json", "{\"ids\":[\"a\"],\"extra\":true}")]
    public async Task HydrateRejectsClosedMalformedOrUnsupportedBodies(string contentType, string json)
    {
        using var app = CreateApp(new ActionModel { Verb = "Read", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, contentType);

        var response = await client.PostAsync("/api/v1/sales-orders/_lookup/hydrate", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HydrateDoesNotRequireAntiforgery()
    {
        var database = new InMemoryBeverDatabaseProvider();
        using var app = CreateApp([new ActionModel { Verb = "Read", Entity = "SalesOrder" }], null, database: database);
        app.MapBeverApi();
        var entity = app.Services.GetRequiredService<BeverRuntime>().Entities.Single();
        var record = await database.CreateAsync(entity, new JsonObject { ["name"] = "Widget" }, default);
        using var client = await CreateClientAsync(app, includeAntiforgery: false);

        var response = await client.PostAsJsonAsync("/api/v1/sales-orders/_lookup/hydrate", new { ids = new[] { record["id"]!.GetValue<string>() } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HydrateRejectsTooManyIdsAndOverlongStringIds()
    {
        using var app = CreateApp(new ActionModel { Verb = "Read", Entity = "SalesOrder" });
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/sales-orders/_lookup/hydrate", new { ids = Enumerable.Range(0, 51).Select(index => index.ToString()).ToArray() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/sales-orders/_lookup/hydrate", new { ids = new[] { new string('a', 257) } })).StatusCode);
    }

    [Fact]
    public async Task DeniedLookupDoesNotInvokeProvider()
    {
        var database = new ThrowingLookupProvider();
        using var app = CreateApp([new ActionModel { Verb = "Read", Entity = "SalesOrder" }], null, null, database, addDefaultAnyoneAuthorization: false);
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var lookup = await client.GetAsync("/api/v1/sales-orders/_lookup");
        var hydrate = await client.PostAsJsonAsync("/api/v1/sales-orders/_lookup/hydrate", new { ids = new[] { "a" } });

        Assert.Equal(HttpStatusCode.Forbidden, lookup.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, hydrate.StatusCode);
        Assert.Equal(0, database.LookupCalls);
    }

    [Fact]
    public void MapsClosedIdentityEndpointsWhenIdentityEnabled()
    {
        using var app = CreateApp(Array.Empty<ActionModel>(), null, null, null, identityEnabled: true, registrationEnabled: true);

        app.MapBeverApi();

        var endpoints = GetRouteEndpoints(app);
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/_identity/login" && HasMethod(endpoint, "POST"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/_identity/logout" && HasMethod(endpoint, "POST"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/_identity/session" && HasMethod(endpoint, "GET"));
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/_identity/register" && HasMethod(endpoint, "POST"));
    }

    [Fact]
    public async Task RegisterRejectsDuplicateWithCommonConflictShape()
    {
        using var app = CreateApp(Array.Empty<ActionModel>(), null, null, null, identityEnabled: true, registrationEnabled: true);
        app.UseAuthentication();
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);
        var payload = new { email = "ada@example.com", password = "correct horse battery staple" };

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/_identity/register", payload)).StatusCode);
        var duplicate = await client.PostAsJsonAsync("/api/v1/_identity/register", payload);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var body = await duplicate.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Conflict", body!["error"]!["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("not-an-email", "correct horse battery staple", "Enter a valid email address.")]
    [InlineData("new@example.com", "short", "Use a password of at least 8 characters.")]
    public async Task RegisterExplainsInvalidEmailsAndShortPasswords(string email, string password, string message)
    {
        using var app = CreateApp(Array.Empty<ActionModel>(), null, null, null, identityEnabled: true, registrationEnabled: true);
        app.UseAuthentication();
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var response = await client.PostAsJsonAsync("/api/v1/_identity/register", new { email, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("ValidationFailed", body!["error"]!["code"]!.GetValue<string>());
        Assert.Equal(message, body["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task RegisterSignsInAndReturnsAuthenticatedSessionShape()
    {
        using var app = CreateApp(Array.Empty<ActionModel>(), null, null, null, identityEnabled: true, registrationEnabled: true);
        app.UseAuthentication();
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var response = await client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "new@example.com", password = "correct horse battery staple" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(body!["authenticated"]!.GetValue<bool>());
        var session = await (await client.GetAsync("/api/v1/_identity/session")).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(session!["authenticated"]!.GetValue<bool>());
        Assert.Equal("new@example.com", session["identity"]!["label"]!.GetValue<string>());
        Assert.False(session["administrator"]!.GetValue<bool>());
    }

    [Fact]
    public async Task IdentityMutationsRequireAntiforgeryEvenWhenAnonymousButSessionDoesNot()
    {
        using var app = CreateApp(Array.Empty<ActionModel>(), null, null, null, identityEnabled: true, registrationEnabled: true);
        app.UseAuthentication();
        app.MapBeverApi();
        using var client = await CreateClientAsync(app, includeAntiforgery: false);

        var register = await client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "csrf@example.com", password = "correct horse battery staple" });
        var login = await client.PostAsJsonAsync("/api/v1/_identity/login", new { email = "csrf@example.com", password = "correct horse battery staple" });
        var logout = await client.PostAsJsonAsync("/api/v1/_identity/logout", new { });
        var session = await client.GetAsync("/api/v1/_identity/session");

        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        var body = await register.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("ValidationFailed", body!["error"]!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task CrudMutationsRequireAntiforgeryEvenWhenAnonymous()
    {
        var database = new InMemoryBeverDatabaseProvider();
        using var app = CreateApp(
            [
                new ActionModel { Verb = "Create", Entity = "SalesOrder" },
                new ActionModel { Verb = "Update", Entity = "SalesOrder" },
                new ActionModel { Verb = "Delete", Entity = "SalesOrder" }
            ],
            null,
            database: database);
        app.MapBeverApi();
        var entity = app.Services.GetRequiredService<BeverRuntime>().Entities.Single();
        var record = await database.CreateAsync(entity, new JsonObject { ["name"] = "Widget" }, default);
        var id = record["id"]!.GetValue<string>();
        using var client = await CreateClientAsync(app, includeAntiforgery: false);

        var create = await client.PostAsJsonAsync("/api/v1/sales-orders", new { name = "Widget" });
        var patch = await client.PatchAsJsonAsync($"/api/v1/sales-orders/{id}", new { name = "Changed" });
        var delete = await client.DeleteAsync($"/api/v1/sales-orders/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
    }

    [Fact]
    public async Task IdentityHydrateDoesNotRequireAntiforgery()
    {
        var store = new InMemoryBeverIdentityStore();
        var identity = (await store.RegisterAsync("picker@example.com", "p", CancellationToken.None)).Identity!;
        using var app = CreateApp(
            [new ActionModel { Verb = "Create", Entity = "SalesOrder" }],
            null,
            identityStore: store,
            identityEnabled: true,
            identityFieldInCreateProjection: true);
        app.MapBeverApi();
        using var client = await CreateClientAsync(app, includeAntiforgery: false);

        var response = await client.PostAsJsonAsync("/api/v1/sales-orders/_identity-lookup/assignee/hydrate?action=Create", new { ids = new[] { identity.Id } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedRequestsRequireLiveIdentityFromStableClaim()
    {
        var store = new ToggleIdentityStore();
        using var app = CreateApp(
            [new ActionModel { Verb = "Read", Entity = "SalesOrder", Authorizations = [new AuthorizationModel { Kind = "Authenticated" }] }],
            null,
            identityStore: store,
            identityEnabled: true,
            registrationEnabled: true);
        app.UseAuthentication();
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "live@example.com", password = "correct horse battery staple" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/_identity/login", new { email = "live@example.com", password = "correct horse battery staple" })).StatusCode);
        store.Live = false;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/sales-orders")).StatusCode);
    }

    [Fact]
    public async Task RotatedSecurityStampInvalidatesExistingCookie()
    {
        var store = new ToggleIdentityStore();
        using var app = CreateApp(
            [new ActionModel { Verb = "Read", Entity = "SalesOrder", Authorizations = [new AuthorizationModel { Kind = "Authenticated" }] }],
            null,
            identityStore: store,
            identityEnabled: true,
            registrationEnabled: true);
        app.UseAuthentication();
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "stamp@example.com", password = "correct horse battery staple" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/sales-orders")).StatusCode);
        store.SessionValidationValue = "rotated";

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/sales-orders")).StatusCode);
    }

    [Fact]
    public async Task UpdatePathAuthorizationUsesStoredRecordNotSubmittedOwner()
    {
        var database = new InMemoryBeverDatabaseProvider();
        using var app = CreateApp(
            [new ActionModel { Verb = "Update", Entity = "SalesOrder", Authorizations = [new AuthorizationModel { Kind = "Path", Path = new ReferencePathModel(["Assignee"]) }] }],
            null,
            database: database,
            identityEnabled: true,
            registrationEnabled: true,
            hasIdentityField: true);
        app.UseAuthentication();
        app.MapBeverApi();
        var entity = app.Services.GetRequiredService<BeverRuntime>().Entities.Single();
        var record = await database.CreateAsync(entity, new JsonObject { ["name"] = "Widget", ["assignee"] = "other" }, default);
        using var client = await CreateClientAsync(app);
        var registered = await (await client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "owner@example.com", password = "correct horse battery staple" })).Content.ReadFromJsonAsync<JsonObject>();
        var identityId = registered!["identity"]!["id"]!.GetValue<string>();

        var response = await client.PatchAsJsonAsync($"/api/v1/sales-orders/{record["id"]!.GetValue<string>()}", new { assignee = identityId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task IdentityPickerIsContextBoundToContainingInputProjectionAndActionQuery()
    {
        using var app = CreateApp(
            [new ActionModel { Verb = "Create", Entity = "SalesOrder" }],
            null,
            identityEnabled: true,
            registrationEnabled: true,
            identityFieldInCreateProjection: true);
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/sales-orders/_identity-lookup/assignee?action=Create")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/sales-orders/_identity-lookup/assignee?action=Read")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/sales-orders/_identity-lookup/assignee/hydrate?action=Create&cursor=bad", new { ids = new[] { "missing" } })).StatusCode);
    }

    [Fact]
    public void IdentityPickerMapsWhenCreateFieldsAreInheritedFromNoPresentation()
    {
        using var app = CreateApp([new ActionModel { Verb = "Create", Entity = "SalesOrder" }], null, identityEnabled: true, hasIdentityField: true);

        app.MapBeverApi();

        Assert.Contains(GetRouteEndpoints(app), endpoint => endpoint.RoutePattern.RawText?.Contains("_identity-lookup", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void IdentityPickerIsNotMappedWhenExplicitProjectionExcludesField()
    {
        using var app = CreateApp(
            [new ActionModel { Verb = "Create", Entity = "SalesOrder" }],
            null,
            new EntityPresentationModel { Creating = new InputExperiencePresentationModel { Mode = "Page", Fields = [new ReferencePathModel(["Name"])] } },
            identityEnabled: true,
            hasIdentityField: true);

        app.MapBeverApi();

        Assert.DoesNotContain(GetRouteEndpoints(app), endpoint => endpoint.RoutePattern.RawText?.Contains("_identity-lookup", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void IdentityPickerIsNotMappedWhenInputModeIsNone()
    {
        using var app = CreateApp(
            [new ActionModel { Verb = "Create", Entity = "SalesOrder" }],
            null,
            new EntityPresentationModel { Creating = new InputExperiencePresentationModel { Mode = "None" } },
            identityEnabled: true,
            hasIdentityField: true);

        app.MapBeverApi();

        Assert.DoesNotContain(GetRouteEndpoints(app), endpoint => endpoint.RoutePattern.RawText?.Contains("_identity-lookup", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ChangedSessionValidationValueMakesSessionUnauthenticated()
    {
        var store = new ToggleIdentityStore();
        using var app = CreateApp(Array.Empty<ActionModel>(), null, identityStore: store, identityEnabled: true, registrationEnabled: true);
        app.UseAuthentication();
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "version@example.com", password = "correct horse battery staple" })).StatusCode);
        store.SessionValidationValue = "changed";

        var session = await (await client.GetAsync("/api/v1/_identity/session")).Content.ReadFromJsonAsync<JsonObject>();

        Assert.False(session!["authenticated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MissingSessionValidationClaimMakesSessionUnauthenticatedWhenStoreProvidesValue()
    {
        var store = new ToggleIdentityStore { SessionValidationValue = null };
        using var app = CreateApp(Array.Empty<ActionModel>(), null, identityStore: store, identityEnabled: true, registrationEnabled: true);
        app.UseAuthentication();
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "missing-claim@example.com", password = "correct horse battery staple" })).StatusCode);
        store.SessionValidationValue = "now-required";

        var session = await (await client.GetAsync("/api/v1/_identity/session")).Content.ReadFromJsonAsync<JsonObject>();

        Assert.False(session!["authenticated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task IdentityLookupReturnsUsableCursor()
    {
        var store = new InMemoryBeverIdentityStore();
        await store.RegisterAsync("alpha@example.com", "p", CancellationToken.None);
        await store.RegisterAsync("beta@example.com", "p", CancellationToken.None);
        await store.RegisterAsync("gamma@example.com", "p", CancellationToken.None);

        var first = await store.LookupAsync("", 2, null, CancellationToken.None);
        var second = await store.LookupAsync("", 2, first.NextCursor, CancellationToken.None);

        Assert.NotNull(first.NextCursor);
        Assert.Equal(["alpha@example.com", "beta@example.com"], first.Items.Select(item => item.Label).ToArray());
        Assert.Equal(["gamma@example.com"], second.Items.Select(item => item.Label).ToArray());
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task AuthenticatedPolicyRequiresLiveIdentity()
    {
        using var app = CreateApp(
            [new ActionModel { Verb = "Read", Entity = "SalesOrder", Authorizations = [new AuthorizationModel { Kind = "Authenticated" }] }],
            null,
            identityEnabled: true);
        app.UseAuthentication();
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        var response = await client.GetAsync("/api/v1/sales-orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingBevAuthorizationRuleForbids()
    {
        using var app = CreateApp(
            [new ActionModel { Verb = "Read", Entity = "SalesOrder" }],
            null,
            addDefaultAnyoneAuthorization: false);
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/sales-orders")).StatusCode);
    }

    [Fact]
    public async Task IdentityFieldAuthorizationForbidsOtherOwner()
    {
        var database = new InMemoryBeverDatabaseProvider();
        using var app = CreateApp(
            [new ActionModel { Verb = "Read", Entity = "SalesOrder", Authorizations = [new AuthorizationModel { Kind = "Path", Path = new ReferencePathModel(["Assignee"]) }] }],
            null,
            database: database,
            identityEnabled: true,
            registrationEnabled: true,
            hasIdentityField: true);
        app.UseAuthentication();
        app.MapBeverApi();
        var entity = app.Services.GetRequiredService<BeverRuntime>().Entities.Single();
        var record = await database.CreateAsync(entity, new JsonObject { ["name"] = "Widget", ["assignee"] = "other" }, default);
        using var client = await CreateClientAsync(app);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "path@example.com", password = "correct horse battery staple" })).StatusCode);

        var response = await client.GetAsync($"/api/v1/sales-orders/{record["id"]!.GetValue<string>()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DirectReadGrantsScopeListsToGrantedRowsOrTheWholeEntity()
    {
        var store = new InMemoryBeverIdentityStore();
        var database = new InMemoryBeverDatabaseProvider();
        using var app = CreateApp(
            [new ActionModel { Verb = "Read", Entity = "SalesOrder" }],
            null,
            database: database,
            identityStore: store,
            identityEnabled: true,
            registrationEnabled: true,
            addDefaultAnyoneAuthorization: false);
        app.UseAuthentication();
        app.MapBeverApi();
        var entity = app.Services.GetRequiredService<BeverRuntime>().Entities.Single();
        var granted = await database.CreateAsync(entity, new JsonObject { ["name"] = "Granted" }, default);
        var other = await database.CreateAsync(entity, new JsonObject { ["name"] = "Other" }, default);
        using var client = await CreateClientAsync(app);
        var registered = await (await client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "grant@example.com", password = "correct horse battery staple" })).Content.ReadFromJsonAsync<JsonObject>();
        var identityId = registered!["identity"]!["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/sales-orders")).StatusCode);

        store.AddDirectGrant(identityId, "SalesOrder", BeverEntityAction.Read, granted["id"]!.GetValue<string>());

        var scoped = (await client.GetFromJsonAsync<JsonArray>("/api/v1/sales-orders"))!;
        Assert.Equal(["Granted"], scoped.Select(record => record!["name"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/sales-orders/{other["id"]!.GetValue<string>()}")).StatusCode);

        store.AddDirectGrant(identityId, "SalesOrder", BeverEntityAction.Read);

        Assert.Equal(2, (await client.GetFromJsonAsync<JsonArray>("/api/v1/sales-orders"))!.Count);
    }

    [Fact]
    public async Task AdministratorFromIdentityStoreRequiresLiveAuthenticationAndBypassesOnlyPolicy()
    {
        var store = new InMemoryBeverIdentityStore();
        using var app = CreateApp(
            [new ActionModel { Verb = "Read", Entity = "SalesOrder", Authorizations = [new AuthorizationModel { Kind = "Authenticated" }] }],
            null,
            identityStore: store,
            identityEnabled: true,
            registrationEnabled: true);
        app.UseAuthentication();
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/sales-orders")).StatusCode);
        var registered = await (await client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "admin@example.com", password = "correct horse battery staple" })).Content.ReadFromJsonAsync<JsonObject>();
        store.AddAdministrator(registered!["identity"]!["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/_identity/login", new { email = "admin@example.com", password = "correct horse battery staple" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/sales-orders")).StatusCode);
    }

    [Fact]
    public async Task DeletePassesActorToProviderWithoutPrePatch()
    {
        var database = new CapturingDeleteProvider();
        using var app = CreateApp(
            [new ActionModel { Verb = "Delete", Entity = "SalesOrder", Authorizations = [new AuthorizationModel { Kind = "Authenticated" }] }],
            null,
            database: database,
            identityEnabled: true,
            registrationEnabled: true);
        app.UseAuthentication();
        app.MapBeverApi();
        using var client = await CreateClientAsync(app);
        var registered = await (await client.PostAsJsonAsync("/api/v1/_identity/register", new { email = "delete@example.com", password = "correct horse battery staple" })).Content.ReadFromJsonAsync<JsonObject>();
        var identityId = registered!["identity"]!["id"]!.GetValue<string>();

        var response = await client.DeleteAsync("/api/v1/sales-orders/record-1");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(identityId, database.ActorId);
        Assert.Equal(0, database.PatchCalls);
    }

    private static WebApplication CreateApp(params ActionModel[] actions) => CreateApp(actions, null);

    private static WebApplication CreateApp(
        IReadOnlyList<ActionModel> actions,
        Action<BeverOptions>? configure,
        EntityPresentationModel? presentation = null,
        IBeverDatabaseProvider? database = null,
        IBeverIdentityStore? identityStore = null,
        bool identityEnabled = false,
        bool registrationEnabled = false,
        bool hasIdentityField = false,
        bool identityFieldInCreateProjection = false,
        bool identityFieldInEditProjection = false,
        bool addDefaultAnyoneAuthorization = true)
    {
        var modelPath = WriteModelFile(actions, presentation, identityEnabled, registrationEnabled, hasIdentityField, identityFieldInCreateProjection, identityFieldInEditProjection, addDefaultAnyoneAuthorization);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetDirectoryName(modelPath),
            EnvironmentName = Environments.Development
        });

        if (database is null)
        {
            builder.Services.AddSingleton<IBeverDatabaseProvider, InMemoryBeverDatabaseProvider>();
        }
        else
        {
            builder.Services.AddSingleton(database);
        }
        if (identityStore is not null)
        {
            builder.Services.AddSingleton(identityStore);
        }
        builder.Services.AddBever(options =>
        {
            options.ModelPath = Path.GetFileName(modelPath);
            configure?.Invoke(options);
        });
        builder.Services.AddSingleton<IAntiforgery, TestAntiforgery>();

        return builder.Build();
    }

    private static string WriteModelFile(IReadOnlyList<ActionModel> actions, EntityPresentationModel? presentation = null, bool identityEnabled = false, bool registrationEnabled = false, bool hasIdentityField = false, bool identityFieldInCreateProjection = false, bool identityFieldInEditProjection = false, bool addDefaultAnyoneAuthorization = true)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bever-aspnetcore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var modelPath = Path.Combine(directory, "model.json");
        hasIdentityField |= identityFieldInCreateProjection || identityFieldInEditProjection;
        if (identityFieldInCreateProjection)
        {
            presentation ??= new EntityPresentationModel();
            presentation!.Creating = new InputExperiencePresentationModel { Mode = "Page", Fields = [new ReferencePathModel(["Assignee"])] };
        }
        if (identityFieldInEditProjection)
        {
            presentation ??= new EntityPresentationModel();
            presentation!.Editing = new InputExperiencePresentationModel { Mode = "Page", Fields = [new ReferencePathModel(["Assignee"])] };
        }

        var fields = new List<FieldModel>
        {
            new()
            {
                Name = "Name",
                Type = new FieldTypeModel
                {
                    Kind = "Primitive",
                    Name = "Text",
                    Arguments = [new Dictionary<string, object?> { ["maxLength"] = 100 }]
                }
            },
            new()
            {
                Name = "Active",
                Type = new FieldTypeModel
                {
                    Kind = "Primitive",
                    Name = "Boolean"
                },
                DefaultValue = new LiteralValueModel { Kind = "Boolean", Value = true }
            },
            new()
            {
                Name = "Note",
                Type = new FieldTypeModel
                {
                    Kind = "Primitive",
                    Name = "Text",
                    Nullable = true,
                    Arguments = [new Dictionary<string, object?> { ["maxLength"] = 100 }]
                }
            }
        };
        if (hasIdentityField)
        {
            fields.Add(new FieldModel { Name = "Assignee", Type = new FieldTypeModel { Kind = "Relationship", TargetKind = "SystemIdentity", Targets = ["Identity"], Nullable = true } });
        }

        var effectiveActions = actions.Select(action => addDefaultAnyoneAuthorization && action.Authorizations.Count == 0
            ? new ActionModel { Verb = action.Verb, Entity = action.Entity, SourceSpan = action.SourceSpan, OrderBy = action.OrderBy, SearchBy = action.SearchBy, Authorizations = [new AuthorizationModel { Kind = "Anyone" }] }
            : action).ToList();

        var model = new BeverModel
        {
            BeverLanguageVersion = "0.6",
            SemanticModelVersion = "0.10",
            Identity = identityEnabled
                ? new IdentityModel(true, registrationEnabled ? ["Login", "Logout", "Session", "Register"] : ["Login", "Logout", "Session"], new IdentitySummaryModel("Email"), [new IdentitySystemTargetModel("Identity", "SystemIdentity", new IdentityLookupModel("IdLabel", true, false))])
                : IdentityModel.Disabled(),
            Entities =
            [
                new EntityModel
                {
                    Name = "SalesOrder",
                    Presentation = presentation,
                    Fields = fields
                }
            ],
            Actions = effectiveActions
        };
        File.WriteAllText(modelPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return modelPath;
    }

    private static IReadOnlyList<RouteEndpoint> GetRouteEndpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(dataSource => dataSource.Endpoints).OfType<RouteEndpoint>().ToList();

    private static bool HasMethod(RouteEndpoint endpoint, string method) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase) == true;

    private static IReadOnlyList<string> EndpointSignatures(WebApplication app) =>
        GetRouteEndpoints(app)
            .Select(endpoint => $"{endpoint.RoutePattern.RawText}:{string.Join(',', endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

    private static async Task<HttpClient> CreateClientAsync(WebApplication app, bool includeAntiforgery = true)
    {
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        if (includeAntiforgery)
        {
            client.DefaultRequestHeaders.Add(TestAntiforgery.HeaderName, TestAntiforgery.Token);
        }

        return client;
    }

    private static async Task<JsonObject> CreateSalesOrderAsync(HttpClient client, object? values = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/sales-orders", values ?? new { name = "Widget" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static async Task<CrudSnapshot> ExerciseCrudAsync(HttpClient client)
    {
        var create = await client.PostAsJsonAsync("/api/v1/sales-orders", new { name = "Widget" });
        var created = (await create.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = created["id"]!.GetValue<string>();
        var list = await client.GetAsync("/api/v1/sales-orders");
        var patch = await client.PatchAsJsonAsync($"/api/v1/sales-orders/{id}", new { name = "Changed" });
        var patched = (await patch.Content.ReadFromJsonAsync<JsonObject>())!;
        var delete = await client.DeleteAsync($"/api/v1/sales-orders/{id}");
        var deletedRead = await client.GetAsync($"/api/v1/sales-orders/{id}");

        return new CrudSnapshot(
            create.StatusCode,
            created["name"]!.GetValue<string>(),
            created["active"]!.GetValue<bool>(),
            list.StatusCode,
            patch.StatusCode,
            patched["name"]!.GetValue<string>(),
            delete.StatusCode,
            deletedRead.StatusCode);
    }

    private static async Task AssertValidationErrorAsync(HttpResponseMessage response, string field, string message)
    {
        var problem = await response.Content.ReadFromJsonAsync<ValidationErrorResponse>(JsonOptions);
        Assert.NotNull(problem);
        Assert.Contains(problem!.Errors, error => error.Field == field && error.Message == message);
    }

    private sealed class ToggleIdentityStore : IBeverIdentityStore
    {
        private readonly InMemoryBeverIdentityStore inner = new();

        public bool Live { get; set; } = true;

        public string? SessionValidationValue { get; set; } = "initial";

        public Task<BeverIdentitySummary?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
            inner.FindByEmailAsync(email, cancellationToken);

        public async Task<BeverIdentitySummary?> FindByIdAsync(string id, CancellationToken cancellationToken) =>
            Live ? await inner.FindByIdAsync(id, cancellationToken) : null;

        public Task<bool> ValidatePasswordAsync(string identityId, string password, CancellationToken cancellationToken) =>
            inner.ValidatePasswordAsync(identityId, password, cancellationToken);

        public Task<BeverIdentityRegistrationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken) =>
            inner.RegisterAsync(email, password, cancellationToken);

        public Task<BeverLookupResult> LookupAsync(string normalizedSearch, int limit, string? cursor, CancellationToken cancellationToken) =>
            inner.LookupAsync(normalizedSearch, limit, cursor, cancellationToken);

        public Task<IReadOnlyList<BeverLookupItem>> HydrateAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
            inner.HydrateAsync(ids, cancellationToken);

        public Task<string?> GetSessionValidationValueAsync(string identityId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(SessionValidationValue);
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

    private sealed class CapturingLookupProvider : IBeverDatabaseProvider
    {
        public BeverLookupRequest? LastLookup { get; private set; }

        public Task<IReadOnlyList<JsonObject>> ListAsync(BeverEntityContext entity, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JsonObject>>([]);

        public Task<JsonObject?> GetAsync(BeverEntityContext entity, string id, CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(null);

        public Task<JsonObject> CreateAsync(BeverEntityContext entity, JsonObject values, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JsonObject?> PatchAsync(BeverEntityContext entity, string id, JsonObject values, CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(null);

        public Task<bool> DeleteAsync(BeverEntityContext entity, string id, string? actorId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<BeverLookupResult> LookupAsync(BeverLookupRequest request, CancellationToken cancellationToken)
        {
            LastLookup = request;
            return Task.FromResult(new BeverLookupResult([], null));
        }

        public Task<IReadOnlyList<BeverLookupItem>> HydrateLookupAsync(BeverLookupHydrationRequest request, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BeverLookupItem>>([]);
    }

    private sealed class ThrowingLookupProvider : IBeverDatabaseProvider
    {
        public int LookupCalls { get; private set; }

        public Task<IReadOnlyList<JsonObject>> ListAsync(BeverEntityContext entity, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JsonObject>>([]);

        public Task<JsonObject?> GetAsync(BeverEntityContext entity, string id, CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(null);

        public Task<JsonObject> CreateAsync(BeverEntityContext entity, JsonObject values, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JsonObject?> PatchAsync(BeverEntityContext entity, string id, JsonObject values, CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(null);

        public Task<bool> DeleteAsync(BeverEntityContext entity, string id, string? actorId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<BeverLookupResult> LookupAsync(BeverLookupRequest request, CancellationToken cancellationToken)
        {
            LookupCalls++;
            throw new InvalidOperationException("Lookup should not be called.");
        }

        public Task<IReadOnlyList<BeverLookupItem>> HydrateLookupAsync(BeverLookupHydrationRequest request, CancellationToken cancellationToken)
        {
            LookupCalls++;
            throw new InvalidOperationException("Hydrate should not be called.");
        }
    }

    private sealed class CapturingDeleteProvider : IBeverDatabaseProvider
    {
        public int PatchCalls { get; private set; }
        public string? ActorId { get; private set; }

        public Task<IReadOnlyList<JsonObject>> ListAsync(BeverEntityContext entity, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JsonObject>>([]);

        public Task<JsonObject?> GetAsync(BeverEntityContext entity, string id, CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(new JsonObject { ["id"] = id, ["name"] = "Widget" });

        public Task<JsonObject> CreateAsync(BeverEntityContext entity, JsonObject values, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JsonObject?> PatchAsync(BeverEntityContext entity, string id, JsonObject values, CancellationToken cancellationToken)
        {
            PatchCalls++;
            return Task.FromResult<JsonObject?>(new JsonObject());
        }

        public Task<bool> DeleteAsync(BeverEntityContext entity, string id, string? actorId, CancellationToken cancellationToken)
        {
            ActorId = actorId;
            return Task.FromResult(true);
        }

        public Task<BeverLookupResult> LookupAsync(BeverLookupRequest request, CancellationToken cancellationToken) => Task.FromResult(new BeverLookupResult([], null));

        public Task<IReadOnlyList<BeverLookupItem>> HydrateLookupAsync(BeverLookupHydrationRequest request, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BeverLookupItem>>([]);
    }

    private sealed record CrudSnapshot(
        HttpStatusCode CreateStatus,
        string CreatedName,
        bool CreatedActive,
        HttpStatusCode ListStatus,
        HttpStatusCode PatchStatus,
        string PatchedName,
        HttpStatusCode DeleteStatus,
        HttpStatusCode DeletedReadStatus);

    private sealed class ValidationErrorResponse
    {
        public List<ValidationError> Errors { get; set; } = [];
    }

    private sealed class ValidationError
    {
        public string Field { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;
    }
}
