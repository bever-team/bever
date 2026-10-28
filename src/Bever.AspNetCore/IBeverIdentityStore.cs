using Microsoft.AspNetCore.Identity;

namespace Bever.AspNetCore;

public interface IBeverIdentityStore
{
    Task<BeverIdentitySummary?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    Task<BeverIdentitySummary?> FindByIdAsync(string id, CancellationToken cancellationToken);

    Task<bool> ValidatePasswordAsync(string identityId, string password, CancellationToken cancellationToken);

    Task<BeverIdentityRegistrationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken);

    Task<BeverLookupResult> LookupAsync(string normalizedSearch, int limit, string? cursor, CancellationToken cancellationToken);

    Task<IReadOnlyList<BeverLookupItem>> HydrateAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);

    Task<string?> GetSessionValidationValueAsync(string identityId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    Task<bool> IsAdministratorAsync(string identityId, CancellationToken cancellationToken) => Task.FromResult(false);

    Task<bool> HasDirectGrantAsync(string identityId, BeverEntityContext entity, BeverEntityAction action, string? recordId, CancellationToken cancellationToken) => Task.FromResult(false);

    Task<IReadOnlyList<string>> ListGrantedRecordIdsAsync(string identityId, BeverEntityContext entity, BeverEntityAction action, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    Task<IReadOnlyList<string>> ListRolesAsync(string identityId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Replaces the account's roles. <paramref name="actorId"/> is the Administrator making the change, for the audit log.</summary>
    Task SetRolesAsync(string identityId, IReadOnlyList<string> roles, string actorId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This identity store cannot assign roles.");
}

public sealed record BeverIdentitySummary(string Id, string Label);

public sealed record BeverIdentityRegistrationResult(bool Created, BeverIdentitySummary? Identity);

public sealed class InMemoryBeverIdentityStore : IBeverIdentityStore
{
    private sealed class Entry
    {
        public required BeverIdentitySummary Summary { get; init; }
        public required string PasswordHash { get; set; }
        public string SessionVersion { get; set; } = "0";
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> byEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> administrators = new(StringComparer.Ordinal);
    private readonly HashSet<(string IdentityId, string Entity, BeverEntityAction Action, string? RecordId)> grants = [];
    private readonly Dictionary<string, List<string>> roles = new(StringComparer.Ordinal);
    private readonly PasswordHasher<Entry> hasher = new();

    public void AddAdministrator(string identityId)
    {
        lock (gate)
        {
            administrators.Add(identityId);
        }
    }

    public void AddDirectGrant(string identityId, string entity, BeverEntityAction action, string? recordId = null)
    {
        lock (gate)
        {
            grants.Add((identityId, entity, action, recordId));
        }
    }

    public void AddRole(string identityId, string role)
    {
        lock (gate)
        {
            if (!roles.TryGetValue(identityId, out var assigned))
            {
                roles[identityId] = assigned = [];
            }

            assigned.Add(role);
        }
    }

    public Task<IReadOnlyList<string>> ListRolesAsync(string identityId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(roles.TryGetValue(identityId, out var assigned) ? assigned.ToList() : []);
        }
    }

    public Task SetRolesAsync(string identityId, IReadOnlyList<string> roles, string actorId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            this.roles[identityId] = roles.ToList();
            return Task.CompletedTask;
        }
    }

    public Task<BeverIdentitySummary?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(byEmail.TryGetValue(email.Trim(), out var entry) ? entry.Summary : null);
        }
    }

    public Task<BeverIdentitySummary?> FindByIdAsync(string id, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(byId.TryGetValue(id, out var entry) ? entry.Summary : null);
        }
    }

    public Task<bool> ValidatePasswordAsync(string identityId, string password, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!byId.TryGetValue(identityId, out var entry))
            {
                return Task.FromResult(false);
            }

            var result = hasher.VerifyHashedPassword(entry, entry.PasswordHash, password);
            return Task.FromResult(result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded);
        }
    }

    public Task<BeverIdentityRegistrationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken)
    {
        email = email.Trim();
        lock (gate)
        {
            if (byEmail.ContainsKey(email))
            {
                return Task.FromResult(new BeverIdentityRegistrationResult(false, null));
            }

            var entry = new Entry { Summary = new BeverIdentitySummary("idn_" + Guid.NewGuid().ToString("N"), email), PasswordHash = string.Empty };
            entry.PasswordHash = hasher.HashPassword(entry, password);
            byId.Add(entry.Summary.Id, entry);
            byEmail.Add(email, entry);
            return Task.FromResult(new BeverIdentityRegistrationResult(true, entry.Summary));
        }
    }

    public Task<BeverLookupResult> LookupAsync(string normalizedSearch, int limit, string? cursor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var result = BeverIdentityLookupCursor.Page(byId.Values
                .Select(entry => new BeverLookupItem(entry.Summary.Id, entry.Summary.Label))
                .Where(item => normalizedSearch.Length == 0 || item.Label.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList(), normalizedSearch, limit, cursor);
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<BeverLookupItem>> HydrateAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<BeverLookupItem>>(ids
                .Where(byId.ContainsKey)
                .Select(id => new BeverLookupItem(id, byId[id].Summary.Label))
                .ToList());
        }
    }

    public Task<bool> IsAdministratorAsync(string identityId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(administrators.Contains(identityId));
        }
    }

    public Task<string?> GetSessionValidationValueAsync(string identityId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(byId.TryGetValue(identityId, out var entry) ? entry.SessionVersion : null);
        }
    }

    public Task<bool> HasDirectGrantAsync(string identityId, BeverEntityContext entity, BeverEntityAction action, string? recordId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(
                grants.Contains((identityId, entity.Model.Name, action, recordId)) ||
                (recordId is not null && grants.Contains((identityId, entity.Model.Name, action, null))));
        }
    }

    public Task<IReadOnlyList<string>> ListGrantedRecordIdsAsync(string identityId, BeverEntityContext entity, BeverEntityAction action, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(grants
                .Where(grant => grant.IdentityId == identityId && grant.Entity == entity.Model.Name && grant.Action == action && grant.RecordId is not null)
                .Select(grant => grant.RecordId!)
                .ToList());
        }
    }
}

internal static class BeverIdentityLookupCursor
{
    public static BeverLookupResult Page(IReadOnlyList<BeverLookupItem> sortedItems, string normalizedSearch, int limit, string? cursor)
    {
        var after = Decode(cursor, normalizedSearch, limit);
        var candidates = after is null
            ? sortedItems
            : sortedItems.Where(item => string.CompareOrdinal(BeverLookupAlgorithms.NormalizeSearchText(item.Label), after.Label) > 0 ||
                (string.Equals(BeverLookupAlgorithms.NormalizeSearchText(item.Label), after.Label, StringComparison.Ordinal) && string.CompareOrdinal(item.Id, after.Id) > 0)).ToList();

        var page = candidates.Take(limit).ToList();
        var next = candidates.Count > limit && page.Count > 0 ? Encode(normalizedSearch, limit, page[^1]) : null;
        return new BeverLookupResult(page, next);
    }

    private static string Encode(string normalizedSearch, int limit, BeverLookupItem last) =>
        Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CursorPayload("1", normalizedSearch, limit, BeverLookupAlgorithms.NormalizeSearchText(last.Label), last.Id)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static CursorPayload? Decode(string? cursor, string normalizedSearch, int limit)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var payload = System.Text.Json.JsonSerializer.Deserialize<CursorPayload>(Convert.FromBase64String(padded));
            return payload is { Version: "1" } && payload.NormalizedSearch == normalizedSearch && payload.Limit == limit && payload.Label is not null && payload.Id is not null
                ? payload
                : throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            throw new BeverLookupInvalidCursorException();
        }
    }

    private sealed record CursorPayload(string Version, string NormalizedSearch, int Limit, string Label, string Id);
}
