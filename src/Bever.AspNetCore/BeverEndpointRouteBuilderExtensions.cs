using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Claims;
using Bever.Model;
using Bever.Sqlite;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bever.AspNetCore;

public static class BeverEndpointRouteBuilderExtensions
{
    public static RouteGroupBuilder MapBeverApi(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<BeverOptions>>().Value;
        var runtime = endpoints.ServiceProvider.GetRequiredService<BeverRuntime>();
        var group = endpoints.MapGroup(NormalizePrefix(options.ApiPrefix));

        foreach (var action in runtime.Actions)
        {
            MapAction(group, action, options.ApiPrefix);
        }

        foreach (var entity in runtime.Entities)
        {
            if (runtime.Actions.Any(action => action.Entity == entity && action.Action is BeverEntityAction.Read or BeverEntityAction.Pick))
            {
                MapLookup(group, entity);
            }
        }

        if (runtime.Model.Identity.Enabled)
        {
            MapIdentity(group, runtime.Model, options.ApiPrefix);
        }

        return group;
    }

    private static void MapAction(RouteGroupBuilder group, BeverActionContext action, string apiPrefix)
    {
        var route = "/" + action.Entity.RouteName;
        var itemRoute = route + "/{id}";

        switch (action.Action)
        {
            case BeverEntityAction.Create:
                group.MapPost(route, async Task<IResult> (JsonObject values, BeverEntityRequestNormalizer normalizer, BeverEntityValidator validator, BeverAuthorizationEvaluator authorization, BeverReferences references, IBeverDatabaseProvider database, CancellationToken cancellationToken) =>
                {
                    var normalized = normalizer.Normalize(action.Entity, values, BeverEntityRequestOperation.Create);
                    var validation = validator.Validate(action.Entity, normalized, BeverEntityRequestOperation.Create);
                    if (!validation.IsValid)
                    {
                        return Results.BadRequest(new { errors = validation.Errors });
                    }

                    var authorizationDecision = await authorization.AuthorizeDetailedAsync(action, null, normalized, null, cancellationToken);
                    if (!authorizationDecision.Allowed)
                    {
                        return Denied(authorizationDecision);
                    }

                    var ruleErrors = validator.ValidateRules(action.Entity, normalized);
                    if (ruleErrors.Count > 0)
                    {
                        return Results.BadRequest(new { errors = ruleErrors });
                    }

                    var referenceErrors = await references.CheckAsync(action.Entity, normalized, null, cancellationToken);
                    if (referenceErrors.Count > 0)
                    {
                        return Results.BadRequest(new { errors = referenceErrors });
                    }

                    ApplyActorFields(normalized, await authorization.GetLiveIdentityAsync(cancellationToken), created: true, modified: true, deleted: false);

                    var record = await database.CreateAsync(action.Entity, normalized, cancellationToken);
                    var id = record["id"]?.GetValue<string>();
                    await references.AddLabelsAsync(action.Entity, [record], cancellationToken);
                    return Results.Created($"{NormalizePrefix(apiPrefix)}{route}/{id}", record);
                }).RequireCookieAntiforgery();
                MapIdentityLookup(group, action, route, hasId: false);
                break;

            case BeverEntityAction.Read:
                group.MapGet(route, async (BeverAuthorizationEvaluator authorization, BeverReferences references, IBeverDatabaseProvider database, CancellationToken cancellationToken) =>
                {
                    var (authorizationDecision, scope) = await authorization.ResolveScopeAsync([action], cancellationToken);
                    if (!authorizationDecision.Allowed)
                    {
                        return Denied(authorizationDecision);
                    }

                    // Limitation: rows are scoped and sorted in memory because lists are unpaged; push both into the provider when lists get paging.
                    var records = (await database.ListAsync(action.Entity, cancellationToken)).Where(record => scope?.Allows(record) ?? true).ToList();
                    records.Sort(BeverRecordOrder.For(action));
                    await references.AddLabelsAsync(action.Entity, records, cancellationToken);
                    return Results.Ok(records);
                });

                group.MapGet(itemRoute, async (string id, BeverAuthorizationEvaluator authorization, BeverReferences references, IBeverDatabaseProvider database, CancellationToken cancellationToken) =>
                {
                    var record = await database.GetAsync(action.Entity, id, cancellationToken);
                    if (record is null)
                    {
                        return Results.NotFound();
                    }

                    var authorizationDecision = await authorization.AuthorizeDetailedAsync(action, id, null, record, cancellationToken);
                    if (!authorizationDecision.Allowed)
                    {
                        return Denied(authorizationDecision);
                    }

                    await references.AddLabelsAsync(action.Entity, [record], cancellationToken);
                    return Results.Ok(record);
                });
                break;

            case BeverEntityAction.Pick:
                // Pickers are served by the entity's lookup routes; they have no CRUD route of their own.
                break;

            case BeverEntityAction.Update:
                group.MapPatch(itemRoute, async Task<IResult> (string id, JsonObject values, BeverEntityRequestNormalizer normalizer, BeverEntityValidator validator, BeverAuthorizationEvaluator authorization, BeverReferences references, IBeverDatabaseProvider database, CancellationToken cancellationToken) =>
                {
                    var earlyAuthorization = !NeedsCurrentRecord(action)
                        ? await authorization.AuthorizeDetailedAsync(action, id, null, null, cancellationToken)
                        : BeverAuthorizationDecision.Allow;
                    if (!earlyAuthorization.Allowed)
                    {
                        return Denied(earlyAuthorization);
                    }

                    var current = await database.GetAsync(action.Entity, id, cancellationToken);
                    if (current is null)
                    {
                        return Results.NotFound();
                    }

                    var normalized = normalizer.Normalize(action.Entity, values, BeverEntityRequestOperation.Patch);
                    var validation = validator.Validate(action.Entity, normalized, BeverEntityRequestOperation.Patch);
                    if (!validation.IsValid)
                    {
                        return Results.BadRequest(new { errors = validation.Errors });
                    }

                    var authorizationDecision = await authorization.AuthorizeDetailedAsync(action, id, null, current, cancellationToken);
                    if (!authorizationDecision.Allowed)
                    {
                        return Denied(authorizationDecision);
                    }

                    // Rules see the record as it will be after the update, after authorization so they reveal nothing of records the caller may not change.
                    var updated = current.DeepClone().AsObject();
                    foreach (var (name, value) in normalized)
                    {
                        updated[name] = value?.DeepClone();
                    }

                    var ruleErrors = validator.ValidateRules(action.Entity, updated);
                    if (ruleErrors.Count > 0)
                    {
                        return Results.BadRequest(new { errors = ruleErrors });
                    }

                    var referenceErrors = await references.CheckAsync(action.Entity, normalized, current, cancellationToken);
                    if (referenceErrors.Count > 0)
                    {
                        return Results.BadRequest(new { errors = referenceErrors });
                    }

                    ApplyActorFields(normalized, await authorization.GetLiveIdentityAsync(cancellationToken), created: false, modified: true, deleted: false);

                    var record = await database.PatchAsync(action.Entity, id, normalized, cancellationToken);
                    if (record is null)
                    {
                        return Results.NotFound();
                    }

                    await references.AddLabelsAsync(action.Entity, [record], cancellationToken);
                    return Results.Ok(record);
                }).RequireCookieAntiforgery();
                MapIdentityLookup(group, action, itemRoute, hasId: true);
                break;

            case BeverEntityAction.Delete:
                group.MapDelete(itemRoute, async (string id, BeverAuthorizationEvaluator authorization, IBeverDatabaseProvider database, CancellationToken cancellationToken) =>
                {
                    var earlyAuthorization = !NeedsCurrentRecord(action)
                        ? await authorization.AuthorizeDetailedAsync(action, id, null, null, cancellationToken)
                        : BeverAuthorizationDecision.Allow;
                    if (!earlyAuthorization.Allowed)
                    {
                        return Denied(earlyAuthorization);
                    }

                    var current = await database.GetAsync(action.Entity, id, cancellationToken);
                    if (current is null)
                    {
                        return Results.NotFound();
                    }

                    var authorizationDecision = await authorization.AuthorizeDetailedAsync(action, id, null, current, cancellationToken);
                    if (!authorizationDecision.Allowed)
                    {
                        return Denied(authorizationDecision);
                    }

                    try
                    {
                        var actorId = (await authorization.GetLiveIdentityAsync(cancellationToken))?.Id;
                        return await database.DeleteAsync(action.Entity, id, actorId, cancellationToken)
                            ? Results.NoContent()
                            : Results.NotFound();
                    }
                    catch (BeverSqliteDeleteRestrictedException exception)
                    {
                        // OnDelete Restrict: name the referencing entity and field, never record IDs.
                        return Results.Json(new { error = new { code = "DeleteRestricted", message = $"{exception.DependentEntity} records still reference this record through {exception.Relationship}." } }, statusCode: StatusCodes.Status409Conflict);
                    }
                }).RequireCookieAntiforgery();
                break;

            default:
                throw new InvalidOperationException($"Unsupported Bever action '{action.Action}'.");
        }
    }

    private static void MapLookup(RouteGroupBuilder group, BeverEntityContext entity)
    {
        var route = "/" + entity.RouteName + "/_lookup";
        group.MapGet(route, async Task<IResult> (HttpRequest request, BeverAuthorizationEvaluator authorization, BeverReferences references, IBeverDatabaseProvider database, CancellationToken cancellationToken) =>
        {
            if (!ValidateLookupQuery(request, out var search, out var limit, out var cursor, out var pickerName, out var error))
            {
                return Results.BadRequest(new { errors = new[] { error } });
            }

            if (references.ResolvePicker(entity, pickerName) is not { } picker)
            {
                return Results.BadRequest(new { errors = new[] { "unknown picker" } });
            }

            var (authorizationDecision, scope) = await authorization.ResolveScopeAsync([picker], cancellationToken);
            if (!authorizationDecision.Allowed)
            {
                return Denied(authorizationDecision);
            }

            var capability = BeverLookupCapability.FromAction(picker);
            try
            {
                var result = await database.LookupAsync(new BeverLookupRequest(entity, capability.LabelField, capability.SearchFields, BeverLookupAlgorithms.NormalizeSearchText(search), limit, cursor, scope, picker.Model.Name ?? string.Empty), cancellationToken);
                return Results.Ok(new { items = result.Items, nextCursor = result.NextCursor });
            }
            catch (BeverLookupInvalidCursorException)
            {
                return Results.BadRequest(new { errors = new[] { "invalid cursor" } });
            }
        });

        group.MapPost(route + "/hydrate", async Task<IResult> (HttpRequest request, BeverAuthorizationEvaluator authorization, BeverReferences references, IBeverDatabaseProvider database, CancellationToken cancellationToken) =>
        {
            var hydration = await TryReadHydrationIdsAsync(request, cancellationToken).ConfigureAwait(false);
            if (!hydration.Success)
            {
                return Results.BadRequest(new { errors = new[] { "invalid hydrate body" } });
            }

            // Labels are visible to anyone who may read the row or pick it through any picker.
            var (authorizationDecision, scope) = await authorization.ResolveScopeAsync(references.SeeingActions(entity), cancellationToken);
            if (!authorizationDecision.Allowed)
            {
                return Denied(authorizationDecision);
            }

            var items = await database.HydrateLookupAsync(new BeverLookupHydrationRequest(entity, BeverReferences.LabelField(entity.Model), hydration.Ids, scope), cancellationToken);
            return Results.Ok(new { items });
        });
    }

    private static void MapIdentity(RouteGroupBuilder group, BeverModel model, string apiPrefix)
    {
        var identity = model.Identity;
        var roles = model.Roles;
        group.MapPost("/_identity/login", async Task<IResult> (HttpContext httpContext, IBeverIdentityStore store, CancellationToken cancellationToken) =>
        {
            var request = await ReadCredentialRequestAsync(httpContext.Request, cancellationToken);
            if (request is null)
            {
                return IdentityError(StatusCodes.Status400BadRequest, "ValidationFailed");
            }

            var found = await store.FindByEmailAsync(request.Email, cancellationToken).ConfigureAwait(false);
            // Verify even for unknown emails so the store can spend equal hashing time.
            var passwordValid = await store.ValidatePasswordAsync(found?.Id ?? string.Empty, request.Password, cancellationToken).ConfigureAwait(false);
            if (found is null || !passwordValid)
            {
                return IdentityError(StatusCodes.Status401Unauthorized, "InvalidCredentials");
            }

            await SignInAsync(httpContext, store, found, cancellationToken).ConfigureAwait(false);
            return Results.Ok(await SessionAsync(store, found, roles, cancellationToken).ConfigureAwait(false));
        }).RequireCookieAntiforgery();

        group.MapPost("/_identity/logout", async Task<IResult> (HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            if (httpContext.Request.ContentLength > 0 && !await IsEmptyObjectBodyAsync(httpContext.Request, cancellationToken).ConfigureAwait(false))
            {
                return IdentityError(StatusCodes.Status400BadRequest, "ValidationFailed");
            }

            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireCookieAntiforgery();

        group.MapGet("/_identity/session", async (HttpContext httpContext, BeverAuthorizationEvaluator authorization, IBeverIdentityStore store, CancellationToken cancellationToken) =>
        {
            var live = await authorization.GetLiveIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
                return Results.Ok(new { authenticated = false, identity = (BeverIdentitySummary?)null, administrator = false, roles = Array.Empty<string>() });
            }

            return Results.Ok(await SessionAsync(store, live, roles, cancellationToken).ConfigureAwait(false));
        });

        if (identity.Capabilities.Contains("Register", StringComparer.Ordinal))
        {
            group.MapPost("/_identity/register", async Task<IResult> (HttpContext httpContext, IBeverIdentityStore store, CancellationToken cancellationToken) =>
            {
                var request = await ReadCredentialRequestAsync(httpContext.Request, cancellationToken);
                if (request is null)
                {
                    return IdentityError(StatusCodes.Status400BadRequest, "ValidationFailed");
                }

                if (!BeverValueFormats.IsValid("Email", request.Email))
                {
                    return IdentityError(StatusCodes.Status400BadRequest, "ValidationFailed", "Enter a valid email address.");
                }

                if (request.Password.EnumerateRunes().Count() < MinimumPasswordLength)
                {
                    return IdentityError(StatusCodes.Status400BadRequest, "ValidationFailed", $"Use a password of at least {MinimumPasswordLength} characters.");
                }

                var result = await store.RegisterAsync(request.Email, request.Password, cancellationToken).ConfigureAwait(false);
                if (!result.Created || result.Identity is null)
                {
                    return IdentityError(StatusCodes.Status409Conflict, "Conflict");
                }

                await SignInAsync(httpContext, store, result.Identity, cancellationToken).ConfigureAwait(false);
                return Results.Created($"{NormalizePrefix(apiPrefix)}/_identity/session", await SessionAsync(store, result.Identity, roles, cancellationToken).ConfigureAwait(false));
            }).RequireCookieAntiforgery();
        }

        if (roles.Count > 0)
        {
            MapAccounts(group, roles);
        }
    }

    private static async Task<object> SessionAsync(IBeverIdentityStore store, BeverIdentitySummary identity, IReadOnlyList<RoleModel> declared, CancellationToken cancellationToken) => new
    {
        authenticated = true,
        identity,
        administrator = await store.IsAdministratorAsync(identity.Id, cancellationToken).ConfigureAwait(false),
        roles = await DeclaredRolesAsync(store, identity.Id, declared, cancellationToken).ConfigureAwait(false)
    };

    // Assignments to roles the model no longer declares allow nothing, so they are not reported.
    private static async Task<IReadOnlyList<string>> DeclaredRolesAsync(IBeverIdentityStore store, string identityId, IReadOnlyList<RoleModel> declared, CancellationToken cancellationToken)
    {
        var assigned = await store.ListRolesAsync(identityId, cancellationToken).ConfigureAwait(false);
        return declared.Where(role => BeverAuthorizationEvaluator.HasRole(assigned, role.Name)).Select(role => role.Name).ToList();
    }

    // Administrators assign roles over HTTP. Administrators and grants themselves stay CLI-only.
    private static void MapAccounts(RouteGroupBuilder group, IReadOnlyList<RoleModel> declared)
    {
        group.MapGet("/_identity/accounts", async Task<IResult> (HttpRequest request, BeverAuthorizationEvaluator authorization, IBeverIdentityStore store, CancellationToken cancellationToken) =>
        {
            var (denied, _) = await RequireAdministratorAsync(authorization, store, cancellationToken).ConfigureAwait(false);
            if (denied is not null)
            {
                return denied;
            }

            if (!ValidateLookupQuery(request, out var search, out var limit, out var cursor, out var picker, out var error) || picker is not null)
            {
                return IdentityError(StatusCodes.Status400BadRequest, "ValidationFailed", error.Length == 0 ? "unsupported query parameter" : error);
            }

            BeverLookupResult page;
            try
            {
                page = await store.LookupAsync(BeverLookupAlgorithms.NormalizeSearchText(search), limit, cursor, cancellationToken).ConfigureAwait(false);
            }
            catch (BeverLookupInvalidCursorException)
            {
                return IdentityError(StatusCodes.Status400BadRequest, "ValidationFailed", "invalid cursor");
            }

            var items = new List<object>();
            foreach (var item in page.Items)
            {
                items.Add(await AccountAsync(store, new BeverIdentitySummary(item.Id, item.Label), declared, cancellationToken).ConfigureAwait(false));
            }

            return Results.Ok(new { items, nextCursor = page.NextCursor });
        });

        group.MapPatch("/_identity/accounts/{id}", async Task<IResult> (string id, HttpRequest request, BeverAuthorizationEvaluator authorization, IBeverIdentityStore store, CancellationToken cancellationToken) =>
        {
            var (denied, administrator) = await RequireAdministratorAsync(authorization, store, cancellationToken).ConfigureAwait(false);
            if (denied is not null)
            {
                return denied;
            }

            var roles = await ReadRolesRequestAsync(request, declared, cancellationToken).ConfigureAwait(false);
            if (roles is null)
            {
                return IdentityError(StatusCodes.Status400BadRequest, "ValidationFailed", "Send { \"roles\": [...] } with declared role names, each once.");
            }

            var account = await store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (account is null)
            {
                return Results.NotFound();
            }

            await store.SetRolesAsync(account.Id, roles, administrator!.Id, cancellationToken).ConfigureAwait(false);
            return Results.Ok(await AccountAsync(store, account, declared, cancellationToken).ConfigureAwait(false));
        }).RequireCookieAntiforgery();
    }

    private static async Task<object> AccountAsync(IBeverIdentityStore store, BeverIdentitySummary account, IReadOnlyList<RoleModel> declared, CancellationToken cancellationToken) => new
    {
        id = account.Id,
        label = account.Label,
        administrator = await store.IsAdministratorAsync(account.Id, cancellationToken).ConfigureAwait(false),
        roles = await DeclaredRolesAsync(store, account.Id, declared, cancellationToken).ConfigureAwait(false)
    };

    private static async Task<(IResult? Denied, BeverIdentitySummary? Administrator)> RequireAdministratorAsync(BeverAuthorizationEvaluator authorization, IBeverIdentityStore store, CancellationToken cancellationToken)
    {
        var live = await authorization.GetLiveIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return (Denied(BeverAuthorizationDecision.UnauthenticatedRequest), null);
        }

        return await store.IsAdministratorAsync(live.Id, cancellationToken).ConfigureAwait(false) ? (null, live) : (Denied(BeverAuthorizationDecision.Forbid), null);
    }

    // A closed { "roles": [...] } body naming declared roles exactly, each at most once.
    private static async Task<IReadOnlyList<string>?> ReadRolesRequestAsync(HttpRequest request, IReadOnlyList<RoleModel> declared, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("roles", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var roles = new List<string>();
            foreach (var item in array.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (name is null || roles.Contains(name, StringComparer.OrdinalIgnoreCase) || !declared.Any(role => role.Name == name))
                {
                    return null;
                }

                roles.Add(name);
            }

            return roles;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task SignInAsync(HttpContext httpContext, IBeverIdentityStore store, BeverIdentitySummary identity, CancellationToken cancellationToken)
    {
        var claims = new List<Claim> { new(BeverAuthorizationEvaluator.IdentityClaim, identity.Id) };
        var validation = await store.GetSessionValidationValueAsync(identity.Id, cancellationToken).ConfigureAwait(false);
        if (validation is not null)
        {
            claims.Add(new Claim(BeverAuthorizationEvaluator.SessionValidationClaim, validation));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal).ConfigureAwait(false);
    }

    private const int MinimumPasswordLength = 8;

    private static IResult IdentityError(int statusCode, string code, string message = "The request is invalid.") =>
        Results.Json(new { error = new { code, message } }, statusCode: statusCode);

    private static async Task<CredentialRequest?> ReadCredentialRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name) || property.Name is not ("email" or "password"))
                {
                    return null;
                }
            }

            if (names.Count != 2 || !document.RootElement.TryGetProperty("email", out var emailNode) || !document.RootElement.TryGetProperty("password", out var passwordNode) || emailNode.ValueKind != JsonValueKind.String || passwordNode.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var email = emailNode.GetString()?.Trim() ?? string.Empty;
            var password = passwordNode.GetString() ?? string.Empty;
            if (email.EnumerateRunes().Count() is < 3 or > 320 || password.EnumerateRunes().Count() is < 1 or > 1024)
            {
                return null;
            }

            return new CredentialRequest(email, password);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> IsEmptyObjectBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return false;
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ApplyActorFields(JsonObject values, BeverIdentitySummary? identity, bool created, bool modified, bool deleted)
    {
        var id = identity?.Id;
        if (created)
        {
            values["createdBy"] = id;
        }
        if (modified)
        {
            values["modifiedBy"] = id;
        }
        if (deleted)
        {
            values["deletedBy"] = id;
        }
    }

    private sealed record CredentialRequest(string Email, string Password);

    private static bool NeedsCurrentRecord(BeverActionContext action) =>
        action.Model.Authorizations.Any(authorization => authorization.Kind == "Path");

    private static void MapIdentityLookup(RouteGroupBuilder group, BeverActionContext action, string route, bool hasId)
    {
        if (!action.Entity.Model.Fields.Any(field => IsIdentityRelationship(field) && IsInInputProjection(action, field)))
        {
            return;
        }

        var lookupRoute = route + "/_identity-lookup/{field}";
        var hydrateRoute = lookupRoute + "/hydrate";

        if (hasId)
        {
            group.MapGet(lookupRoute, (string id, string field, HttpRequest request, BeverAuthorizationEvaluator authorization, IBeverDatabaseProvider database, IBeverIdentityStore store, CancellationToken cancellationToken) =>
                HandleIdentityLookup(action, id, field, request, authorization, database, store, cancellationToken));
            group.MapPost(hydrateRoute, (string id, string field, HttpRequest request, BeverAuthorizationEvaluator authorization, IBeverDatabaseProvider database, IBeverIdentityStore store, CancellationToken cancellationToken) =>
                HandleIdentityHydrate(action, id, field, request, authorization, database, store, cancellationToken));
        }
        else
        {
            group.MapGet(lookupRoute, (string field, HttpRequest request, BeverAuthorizationEvaluator authorization, IBeverIdentityStore store, CancellationToken cancellationToken) =>
                HandleIdentityLookup(action, null, field, request, authorization, null, store, cancellationToken));
            group.MapPost(hydrateRoute, (string field, HttpRequest request, BeverAuthorizationEvaluator authorization, IBeverIdentityStore store, CancellationToken cancellationToken) =>
                HandleIdentityHydrate(action, null, field, request, authorization, null, store, cancellationToken));
        }
    }

    private static async Task<IResult> HandleIdentityLookup(BeverActionContext action, string? id, string field, HttpRequest request, BeverAuthorizationEvaluator authorization, IBeverDatabaseProvider? database, IBeverIdentityStore store, CancellationToken cancellationToken)
    {
        if (!ValidateIdentityLookupQuery(request, action.Action, out var search, out var limit, out var cursor, out var error) || !HasIdentityField(action, field))
        {
            return Results.BadRequest(new { errors = new[] { error.Length == 0 ? "invalid identity lookup" : error } });
        }

        var current = id is not null && database is not null ? await database.GetAsync(action.Entity, id, cancellationToken).ConfigureAwait(false) : null;
        if (id is not null && current is null)
        {
            return Results.NotFound();
        }

        var authorizationDecision = await authorization.AuthorizeDetailedAsync(action, id, null, current, cancellationToken).ConfigureAwait(false);
        if (!authorizationDecision.Allowed)
        {
            return Denied(authorizationDecision);
        }

        try
        {
            var result = await store.LookupAsync(BeverLookupAlgorithms.NormalizeSearchText(search), limit, cursor, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { items = result.Items, nextCursor = result.NextCursor });
        }
        catch (BeverLookupInvalidCursorException)
        {
            return Results.BadRequest(new { errors = new[] { "invalid cursor" } });
        }
    }

    private static async Task<IResult> HandleIdentityHydrate(BeverActionContext action, string? id, string field, HttpRequest request, BeverAuthorizationEvaluator authorization, IBeverDatabaseProvider? database, IBeverIdentityStore store, CancellationToken cancellationToken)
    {
        if (!ValidateHydrateActionQuery(request, action.Action) || !HasIdentityField(action, field))
        {
            return Results.BadRequest(new { errors = new[] { "invalid identity hydrate" } });
        }

        var hydration = await TryReadHydrationIdsAsync(request, cancellationToken).ConfigureAwait(false);
        if (!hydration.Success)
        {
            return Results.BadRequest(new { errors = new[] { "invalid hydrate body" } });
        }

        var current = id is not null && database is not null ? await database.GetAsync(action.Entity, id, cancellationToken).ConfigureAwait(false) : null;
        if (id is not null && current is null)
        {
            return Results.NotFound();
        }

        var authorizationDecision = await authorization.AuthorizeDetailedAsync(action, id, null, current, cancellationToken).ConfigureAwait(false);
        if (!authorizationDecision.Allowed)
        {
            return Denied(authorizationDecision);
        }

        var items = await store.HydrateAsync(hydration.Ids, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { items });
    }

    private static bool IsIdentityRelationship(FieldModel field) => field.Type is { Kind: "Relationship", TargetKind: "SystemIdentity" };

    private static IResult Denied(BeverAuthorizationDecision decision) => decision.Unauthenticated
        ? Results.Json(new { error = new { code = "Unauthenticated", message = "The request is invalid." } }, statusCode: StatusCodes.Status401Unauthorized)
        : Results.StatusCode(StatusCodes.Status403Forbidden);

    private static bool HasIdentityField(BeverActionContext action, string field) =>
        action.Entity.Model.Fields.Any(candidate =>
            IsIdentityRelationship(candidate) &&
            string.Equals(BeverEntityRequestNormalizer.GetRequestFieldName(candidate), field, StringComparison.OrdinalIgnoreCase) &&
            IsInInputProjection(action, candidate));

    private static bool IsInInputProjection(BeverActionContext action, FieldModel field)
    {
        var input = action.Action switch
        {
            BeverEntityAction.Create => action.Entity.Model.Presentation?.Creating,
            BeverEntityAction.Update => action.Entity.Model.Presentation?.Editing,
            _ => null
        };

        if (input is null)
        {
            return true;
        }

        if (string.Equals(input.Mode, "None", StringComparison.Ordinal))
        {
            return false;
        }

        return input.Fields is null || ContainsDirectField(input.Fields, field.Name) || input.Inputs.Any(candidate => ContainsDirectField(candidate.Field, field.Name));
    }

    private static bool ContainsDirectField(IEnumerable<ReferencePathModel>? paths, string fieldName) =>
        paths?.Any(path => ContainsDirectField(path, fieldName)) == true;

    private static bool ContainsDirectField(ReferencePathModel? path, string fieldName) =>
        path is { Segments.Count: 1 } && string.Equals(path.Segments[0], fieldName, StringComparison.OrdinalIgnoreCase);

    public static RouteGroupBuilder MapBeverMetadata(this IEndpointRouteBuilder endpoints, string prefix = "/_bever")
    {
        var group = endpoints.MapGroup(NormalizePrefix(prefix));

        group.MapGet("/model", (BeverRuntime runtime) => Results.Ok(runtime.Model));
        group.MapGet("/entities", (BeverRuntime runtime) => Results.Ok(runtime.Entities.Select(entity => new
        {
            entity.Model.Name,
            entity.RouteName,
            entity.Model.Fields
        })));
        group.MapGet("/entities/{entityRoute}", (string entityRoute, BeverRuntime runtime) =>
            runtime.TryGetEntity(entityRoute, out var entity) ? Results.Ok(entity.Model) : Results.NotFound());

        return group;
    }

    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix == "/")
        {
            return string.Empty;
        }

        return "/" + prefix.Trim('/');
    }

    private static RouteHandlerBuilder RequireCookieAntiforgery(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            try
            {
                await httpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(httpContext).ConfigureAwait(false);
            }
            catch (AntiforgeryValidationException)
            {
                return IdentityError(StatusCodes.Status400BadRequest, "ValidationFailed");
            }

            return await next(context).ConfigureAwait(false);
        });

    private static bool ValidateLookupQuery(HttpRequest request, out string search, out int limit, out string? cursor, out string? picker, out string error)
    {
        search = string.Empty;
        limit = 20;
        cursor = null;
        picker = request.Query.TryGetValue("picker", out var rawPicker) ? rawPicker.ToString() : null;
        error = string.Empty;
        foreach (var key in request.Query.Keys)
        {
            if (key is not ("search" or "limit" or "cursor" or "picker"))
            {
                error = "unsupported query parameter";
                return false;
            }

            if (request.Query[key].Count != 1)
            {
                error = "duplicate query parameter";
                return false;
            }
        }

        search = request.Query["search"].ToString();
        if (search.EnumerateRunes().Count() > 200)
        {
            error = "search is too long";
            return false;
        }

        if (request.Query.TryGetValue("limit", out var rawLimit) && (!int.TryParse(rawLimit.ToString(), out limit) || limit is < 1 or > 50))
        {
            error = "limit must be between 1 and 50";
            return false;
        }

        cursor = request.Query["cursor"].ToString();
        if (cursor.Length == 0)
        {
            cursor = null;
        }
        else if (cursor.Length > 2048 || cursor.Any(ch => !IsBase64UrlChar(ch)))
        {
            error = "invalid cursor";
            return false;
        }

        return true;
    }

    private static bool ValidateIdentityLookupQuery(HttpRequest request, BeverEntityAction expectedAction, out string search, out int limit, out string? cursor, out string error)
    {
        search = string.Empty;
        limit = 20;
        cursor = null;
        error = string.Empty;
        if (!ValidateRequiredActionQuery(request, expectedAction))
        {
            error = "invalid action";
            return false;
        }

        foreach (var key in request.Query.Keys)
        {
            if (key is not ("action" or "search" or "limit" or "cursor") || request.Query[key].Count != 1)
            {
                error = "unsupported query parameter";
                return false;
            }
        }

        search = request.Query["search"].ToString();
        if (search.EnumerateRunes().Count() > 200)
        {
            error = "search is too long";
            return false;
        }

        if (request.Query.TryGetValue("limit", out var rawLimit) && (!int.TryParse(rawLimit.ToString(), out limit) || limit is < 1 or > 50))
        {
            error = "limit must be between 1 and 50";
            return false;
        }

        cursor = request.Query["cursor"].ToString();
        if (cursor.Length == 0)
        {
            cursor = null;
        }
        else if (cursor.Length > 2048 || cursor.Any(ch => !IsBase64UrlChar(ch)))
        {
            error = "invalid cursor";
            return false;
        }

        return true;
    }

    private static bool ValidateRequiredActionQuery(HttpRequest request, BeverEntityAction expectedAction)
    {
        if (!request.Query.TryGetValue("action", out var rawAction) || rawAction.Count != 1 || !string.Equals(rawAction.ToString(), expectedAction.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        return request.Query.Keys.All(key => key is "action" or "search" or "limit" or "cursor");
    }

    private static bool ValidateHydrateActionQuery(HttpRequest request, BeverEntityAction expectedAction) =>
        request.Query.Count == 1 &&
        request.Query.TryGetValue("action", out var rawAction) &&
        rawAction.Count == 1 &&
        string.Equals(rawAction.ToString(), expectedAction.ToString(), StringComparison.Ordinal);

    private static async Task<HydrationParseResult> TryReadHydrationIdsAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return HydrationParseResult.Fail;
        }

        try
        {
            var body = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (body is null || body.Count != 1 || !body.TryGetPropertyValue("ids", out var idsNode) || idsNode is not JsonArray idsArray || idsArray.Count is < 1 or > 50)
            {
                return HydrationParseResult.Fail;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ids = new List<string>();
            foreach (var node in idsArray)
            {
                if (node is not JsonValue value || !value.TryGetValue<string>(out var id))
                {
                    return HydrationParseResult.Fail;
                }

                if (id.Length == 0 || id.EnumerateRunes().Count() > 256)
                {
                    return HydrationParseResult.Fail;
                }

                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }

            return new HydrationParseResult(true, ids);
        }
        catch (JsonException)
        {
            return HydrationParseResult.Fail;
        }
    }

    private static bool IsBase64UrlChar(char ch) => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';

    private sealed record HydrationParseResult(bool Success, IReadOnlyList<string> Ids)
    {
        public static HydrationParseResult Fail { get; } = new(false, []);
    }

    private sealed record BeverLookupCapability(string LabelField, IReadOnlyList<string> SearchFields)
    {
        public static BeverLookupCapability FromAction(BeverActionContext action)
        {
            var label = BeverReferences.LabelField(action.Entity.Model);
            var search = action.Model.SearchBy is { Count: > 0 }
                ? action.Model.SearchBy.Select(path => path.Segments[0]).ToList()
                : IsSearchable(action.Entity.Model.Fields.FirstOrDefault(field => string.Equals(field.Name, label, StringComparison.OrdinalIgnoreCase))?.Type) ? [label] : [];
            return new BeverLookupCapability(label, search);
        }

        private static bool IsSearchable(FieldTypeModel? type) =>
            type?.Kind == "Enum" || (type?.Kind == "Primitive" && type.Name is "Text" or "Email" or "Phone" or "Url");
    }
}
