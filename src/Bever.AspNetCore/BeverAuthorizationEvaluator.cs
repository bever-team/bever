using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Bever.AspNetCore;

internal sealed class BeverAuthorizationEvaluator(IHttpContextAccessor httpContextAccessor, IBeverIdentityStore identities)
{
    public const string IdentityClaim = "bever_identity_id";
    public const string SessionValidationClaim = "bever_session_validation";

    public async Task<BeverIdentitySummary?> GetLiveIdentityAsync(CancellationToken cancellationToken)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var id = user.FindFirstValue(IdentityClaim);
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var identity = await identities.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return null;
        }

        var actual = await identities.GetSessionValidationValueAsync(id, cancellationToken).ConfigureAwait(false);
        if (actual is null)
        {
            return identity;
        }

        var expected = user.FindFirstValue(SessionValidationClaim);
        return string.Equals(actual, expected, StringComparison.Ordinal) ? identity : null;
    }

    public async Task<bool> AuthorizeAsync(BeverActionContext action, string? recordId, JsonObject? normalizedCreateValues, JsonObject? currentRecord, CancellationToken cancellationToken) =>
        (await AuthorizeDetailedAsync(action, recordId, normalizedCreateValues, currentRecord, cancellationToken).ConfigureAwait(false)).Allowed;

    public async Task<BeverAuthorizationDecision> AuthorizeDetailedAsync(BeverActionContext action, string? recordId, JsonObject? normalizedCreateValues, JsonObject? currentRecord, CancellationToken cancellationToken)
    {
        var identity = await GetLiveIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identity is null && httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true)
        {
            return BeverAuthorizationDecision.UnauthenticatedRequest;
        }

        var requiredLiveIdentity = false;
        IReadOnlyList<string>? roles = null;

        foreach (var rule in action.Model.Authorizations)
        {
            if (rule.Kind == "Anyone")
            {
                return BeverAuthorizationDecision.Allow;
            }

            if (rule.Kind == "Authenticated" && identity is not null)
            {
                return BeverAuthorizationDecision.Allow;
            }

            if (rule.Kind == "Role" && identity is not null && HasRole(roles ??= await identities.ListRolesAsync(identity.Id, cancellationToken).ConfigureAwait(false), rule.Role))
            {
                return BeverAuthorizationDecision.Allow;
            }

            requiredLiveIdentity |= rule.Kind is "Authenticated" or "Role";

            if (rule.Kind == "Path" && identity is not null && rule.Path is not null)
            {
                var field = action.Entity.Model.Fields.First(candidate => string.Equals(candidate.Name, rule.Path.Segments[0], StringComparison.OrdinalIgnoreCase));
                var name = BeverEntityRequestNormalizer.GetRequestFieldName(field);
                var source = action.Action == BeverEntityAction.Create ? normalizedCreateValues : currentRecord;
                if (source?[name]?.GetValue<string>() == identity.Id)
                {
                    return BeverAuthorizationDecision.Allow;
                }
            }

            requiredLiveIdentity |= rule.Kind == "Path";
        }

        if (identity is null)
        {
            if (requiredLiveIdentity)
            {
                return BeverAuthorizationDecision.UnauthenticatedRequest;
            }

            return BeverAuthorizationDecision.Forbid;
        }

        var canUseDirectGrant = action.Action != BeverEntityAction.Read || recordId is not null;
        if (await identities.IsAdministratorAsync(identity.Id, cancellationToken).ConfigureAwait(false) ||
            (canUseDirectGrant && await identities.HasDirectGrantAsync(identity.Id, action.Entity, action.Action, recordId, cancellationToken).ConfigureAwait(false)))
        {
            return BeverAuthorizationDecision.Allow;
        }

        return BeverAuthorizationDecision.Forbid;
    }

    // Which rows the caller may see through any of these Read/Pick actions. Path rules and record grants
    // filter rows instead of denying the request; a null scope means every row.
    public async Task<(BeverAuthorizationDecision Decision, BeverRowScope? Scope)> ResolveScopeAsync(IReadOnlyList<BeverActionContext> actions, CancellationToken cancellationToken)
    {
        var identity = await GetLiveIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (actions.Count == 0)
        {
            return (BeverAuthorizationDecision.Forbid, null);
        }

        if (identity is null && httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true)
        {
            return (BeverAuthorizationDecision.UnauthenticatedRequest, null);
        }

        var ownerFields = new List<string>();
        var recordIds = new List<string>();
        var requiredLiveIdentity = false;
        var administrator = identity is not null && await identities.IsAdministratorAsync(identity.Id, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string>? roles = null;
        foreach (var action in actions)
        {
            foreach (var rule in action.Model.Authorizations)
            {
                if (rule.Kind == "Anyone" || (rule.Kind == "Authenticated" && identity is not null) ||
                    (rule.Kind == "Role" && identity is not null && HasRole(roles ??= await identities.ListRolesAsync(identity.Id, cancellationToken).ConfigureAwait(false), rule.Role)))
                {
                    return (BeverAuthorizationDecision.Allow, null);
                }

                requiredLiveIdentity |= rule.Kind is "Authenticated" or "Path" or "Role";
                if (rule is { Kind: "Path", Path.Segments.Count: 1 })
                {
                    ownerFields.Add(rule.Path.Segments[0]);
                }
            }

            if (identity is null)
            {
                continue;
            }

            if (administrator || await identities.HasDirectGrantAsync(identity.Id, action.Entity, action.Action, null, cancellationToken).ConfigureAwait(false))
            {
                return (BeverAuthorizationDecision.Allow, null);
            }

            recordIds.AddRange(await identities.ListGrantedRecordIdsAsync(identity.Id, action.Entity, action.Action, cancellationToken).ConfigureAwait(false));
        }

        if (identity is null)
        {
            return (requiredLiveIdentity ? BeverAuthorizationDecision.UnauthenticatedRequest : BeverAuthorizationDecision.Forbid, null);
        }

        return ownerFields.Count == 0 && recordIds.Count == 0
            ? (BeverAuthorizationDecision.Forbid, null)
            : (BeverAuthorizationDecision.Allow, new BeverRowScope(identity.Id, ownerFields.Distinct().ToList(), recordIds.Distinct().ToList()));
    }

    // Assignments keep the casing the role had when assigned; declarations are unique ignoring case.
    internal static bool HasRole(IReadOnlyList<string> roles, string? role) =>
        roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}

internal sealed record BeverAuthorizationDecision(bool Allowed, bool Unauthenticated)
{
    public static BeverAuthorizationDecision Allow { get; } = new(true, false);
    public static BeverAuthorizationDecision Forbid { get; } = new(false, false);
    public static BeverAuthorizationDecision UnauthenticatedRequest { get; } = new(false, true);
}
