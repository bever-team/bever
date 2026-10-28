using Bever.Sqlite;
using Microsoft.Data.Sqlite;

namespace Bever.AspNetCore;

internal sealed class SqliteBeverIdentityStore(BeverSqliteDatabase database) : IBeverIdentityStore
{
    public async Task<BeverIdentitySummary?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
        ToSummary(await database.FindIdentityByEmailAsync(email, cancellationToken).ConfigureAwait(false));

    public async Task<BeverIdentitySummary?> FindByIdAsync(string id, CancellationToken cancellationToken) =>
        ToSummary(await database.FindIdentityByIdAsync(id, cancellationToken).ConfigureAwait(false));

    public Task<bool> ValidatePasswordAsync(string identityId, string password, CancellationToken cancellationToken) =>
        database.VerifyIdentityPasswordAsync(identityId, password, cancellationToken);

    public async Task<BeverIdentityRegistrationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (await database.FindIdentityByEmailAsync(email, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new BeverIdentityRegistrationResult(false, null);
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var identity = await database.RegisterIdentityAsync(email, password, cancellationToken).ConfigureAwait(false);
                return new BeverIdentityRegistrationResult(true, ToSummary(identity));
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                return new BeverIdentityRegistrationResult(false, null);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                if (await database.FindIdentityByEmailAsync(email, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return new BeverIdentityRegistrationResult(false, null);
                }

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }

        return new BeverIdentityRegistrationResult(false, null);
    }

    public async Task<BeverLookupResult> LookupAsync(string normalizedSearch, int limit, string? cursor, CancellationToken cancellationToken)
    {
        var items = (await database.ListActiveIdentitiesAsync(cancellationToken).ConfigureAwait(false))
            .Select(identity => new BeverLookupItem(identity.Id, identity.Label))
            .Where(item => normalizedSearch.Length == 0 || item.Label.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
        return BeverIdentityLookupCursor.Page(items, normalizedSearch, limit, cursor);
    }

    public async Task<IReadOnlyList<BeverLookupItem>> HydrateAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var byId = (await database.ListActiveIdentitiesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(identity => identity.Id, StringComparer.Ordinal);
        return ids
            .Distinct(StringComparer.Ordinal)
            .Where(byId.ContainsKey)
            .Select(id => new BeverLookupItem(id, byId[id].Label))
            .ToList();
    }

    public Task<bool> IsAdministratorAsync(string identityId, CancellationToken cancellationToken) =>
        database.IsAdministratorAsync(identityId, cancellationToken);

    public async Task<string?> GetSessionValidationValueAsync(string identityId, CancellationToken cancellationToken)
    {
        var identity = await database.FindIdentityByIdAsync(identityId, cancellationToken).ConfigureAwait(false);
        return identity is null ? null : $"{identity.SessionVersion}:{identity.SecurityStamp}";
    }

    public async Task<bool> HasDirectGrantAsync(string identityId, BeverEntityContext entity, BeverEntityAction action, string? recordId, CancellationToken cancellationToken)
    {
        var grants = await database.ListDirectGrantsAsync(identityId, cancellationToken).ConfigureAwait(false);
        return grants.Any(grant =>
            string.Equals(grant.Entity, entity.Model.Name, StringComparison.Ordinal) &&
            string.Equals(grant.Action, action.ToString(), StringComparison.Ordinal) &&
            (grant.RecordId is null || string.Equals(grant.RecordId, recordId, StringComparison.Ordinal)));
    }

    public async Task<IReadOnlyList<string>> ListGrantedRecordIdsAsync(string identityId, BeverEntityContext entity, BeverEntityAction action, CancellationToken cancellationToken)
    {
        var grants = await database.ListDirectGrantsAsync(identityId, cancellationToken).ConfigureAwait(false);
        return grants
            .Where(grant =>
                string.Equals(grant.Entity, entity.Model.Name, StringComparison.Ordinal) &&
                string.Equals(grant.Action, action.ToString(), StringComparison.Ordinal) &&
                grant.RecordId is not null)
            .Select(grant => grant.RecordId!)
            .ToList();
    }

    public Task<IReadOnlyList<string>> ListRolesAsync(string identityId, CancellationToken cancellationToken) =>
        database.ListRolesAsync(identityId, cancellationToken);

    public Task SetRolesAsync(string identityId, IReadOnlyList<string> roles, string actorId, CancellationToken cancellationToken) =>
        database.SetRolesAsync(identityId, roles, actorId, cancellationToken);

    private static BeverIdentitySummary? ToSummary(BeverSqliteIdentityRecord? identity) =>
        identity is null ? null : new BeverIdentitySummary(identity.Id, identity.Label);
}
