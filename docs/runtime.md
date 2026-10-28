# Runtime

How `Bever.AspNetCore` turns the semantic model into an HTTP API. The routes and payloads are documented for users in the [HTTP API reference](https://bever.dev/reference/http-api); this document records the rules behind them, so changes keep them intact. See [architecture.md](architecture.md) for the overall picture.

## Routes come from actions

`MapBeverApi()` maps one route per declared action, plus the sign-in and account routes of `Identity`, and nothing else. An entity without `Read` has no list or detail route; presentation never adds or removes a route. Entity routes are the kebab-case plural of the entity name (`EntityRouteName`); two entities that map to the same route are a model error. `_lookup`, `_identity-lookup`, and `_identity` are reserved route segments.

`MapBeverMetadata()` (`/_bever/model`, `/_bever/entities`) is opt-in and exposes the full model; hosts decide whether to map it.

## Requests

Request bodies are closed: unknown fields, system fields, and `_labels` are rejected with `400`. On create, omitted fields take their defaults and required fields must be present; on patch, only sent fields change and `null` clears an optional field. Values are normalized to canonical formats (`BeverValueFormats`): enums accept an option's integer value and store its name, `DateTime` is stored as UTC.

Validation errors are `400 { "errors": [{ "field", "message" }] }`, with one message per field explaining how to fix it.

Entity `Rule`s are checked by `BeverEntityValidator.ValidateRules` after value validation and authorization, against the record as it will be: the normalized create body, or the stored record with the patch applied. Checking after authorization keeps a rule from revealing anything about a record the caller may not change. Conditions use SQL `CHECK` logic: a comparison with an empty value is unknown and only a false rule fails. A failed rule is a validation error on the rule's first field with the rule's message. `Today` and `Now` are UTC.

## Authorization

`BeverAuthorizationEvaluator` is the only place that decides. `.bev` rules are not replaceable by hosts or providers.

| Rule | Allows |
| --- | --- |
| `By Anyone` | every caller |
| `By Authenticated` | a caller with a live identity |
| `By <Role>` | a live identity assigned the role |
| `By <Field>` | a live identity equal to the record's `-> Identity` field |

- **Roles** are checked against the account's assignments on every request (`IBeverIdentityStore.ListRolesAsync`), so a revoked role stops working at once. Assignments compare ignoring case, matching how declarations are unique. Removing a role from the model leaves its assignments in place without effect; declaring it again restores them.
- **Create** compares the field with the normalized request value. **Update** and **Delete** compare with the stored record, so a request cannot take over a record by changing its owner.
- A live identity is a signed-in account that exists, is not disabled or deleted, and whose session validation claim (`bever_session_validation`, the account's session version and security stamp) still matches. A cookie for an account that is no longer live is treated as unauthenticated.
- **Administrators** may perform every action. **Grants** allow one action on a whole entity or on one record. Both are additive; they never bypass validation, antiforgery, reference checks, or storage constraints. They are managed only by owner-run CLI commands (`bever identity admin|grant`); there is no HTTP, UI, or SDK surface for them. Role assignments are managed from the CLI (`bever identity role`) and, by Administrators, over HTTP (see [Accounts](#accounts)).
- A caller without a live identity where one is needed gets `401`; a live identity that is not allowed gets `403`.

### Row scope

On `Read` and `Pick`, owner rules filter instead of refusing. `ResolveScopeAsync` resolves which rows a caller may see through a set of actions:

- a matching `Anyone` rule, an `Authenticated` rule with a live identity, a role the caller is assigned, an Administrator, or an entity-wide grant: every row (`null` scope);
- otherwise, for a live identity: rows whose field holds the caller, plus rows named by the caller's record grants for that verb (`BeverRowScope { ActorId, OwnerFields, RecordIds }`), which may be empty;
- otherwise `401` (a rule needed an identity) or `403`.

Lists, lookups, hydration, and label resolution all apply the scope. Providers receive it and must filter by it.

## References and pickers

A relationship field picks with its named picker (`Pick <Name>`), otherwise the target's default `Pick`, otherwise the target's `Read` (`BeverReferences.ResolvePicker`). A picker returns only `{ id, label }` summaries and never grants `Read`.

- **Saving.** `POST` and `PATCH` check every submitted non-null single-target reference after validation and authorization: the target must be in the caller's scope for the field's picker; `-> Identity` values must be live accounts. Missing, deleted, and unpickable targets all fail with the same `"The selected record is unavailable."`, so saves never reveal which rows exist. A `PATCH` value equal to the stored value is not re-checked.
- **Labels.** Every record returned by list, get, create, and patch carries a response-only `_labels` object: each set single-target reference mapped to the target's label, or `null` when the caller may neither read nor pick the target through any of its pickers (or it is gone). Identity labels are included for live accounts, because reading the containing record is what lets the caller see them.
- **Record labels** come from the target's `RecordLabel`; when omitted, the first `Text` field, otherwise the first other `Email`, `Phone`, `Url`, `Int`, `Decimal`, `Date`, `DateTime`, or enum field. Never the record ID.

### Lookup and hydration

For entities with `Read` or `Pick`:

```text
GET  /{entity}/_lookup?picker=<Name>&search=<text>&limit=<n>&cursor=<token>
POST /{entity}/_lookup/hydrate      { "ids": [...] }
```

- `picker` names a named `Pick` (case-insensitive); omitted, the default picker applies. An unknown picker, or none, is `400`.
- Lookup returns `{ items: [{ id, label }], nextCursor }`. Hydration returns `{ items }` for the IDs the caller may see through `Read` or any picker, in request order after removing duplicates; missing, deleted, and invisible IDs are omitted without distinction. No route returns full records, counts, or whole lists.
- **Search** uses the picker's `SearchBy` fields, otherwise the record label when it is `Text`, `Email`, `Phone`, `Url`, or enum; otherwise lookup is browse-only. Normalization trims, collapses whitespace runs to one space, and lowercases ASCII `A`–`Z` only. Matches are substring matches; prefix matches rank first, then normalized label, then ID, all ordinal. `Read OrderBy` does not affect lookups.
- **Cursors** are opaque base64url JSON, version `1`, bound to the entity, picker, normalized search, search fields, and limit, and holding the last rank, label, and ID. A malformed or mismatched cursor is `400`. Pages are deterministic per request, not snapshots.
- **Bounds** are fixed: limit 1–50, default 20; search at most 200 Unicode scalar values; cursor at most 2048 characters; hydration 1–50 IDs of at most 256 scalar values each. Unknown or repeated query parameters are `400`.

### Account pickers

`Identity` is a built-in target, not an entity, and there is no `Read Identity` or account directory. Account pickers exist only under the Create and Update routes of an entity, for `-> Identity` fields in that action's input projection:

```text
GET  /{entity}/_identity-lookup/{field}?action=Create&...
GET  /{entity}/{id}/_identity-lookup/{field}?action=Update&...
POST /{entity}/_identity-lookup/{field}/hydrate?action=Create
POST /{entity}/{id}/_identity-lookup/{field}/hydrate?action=Update
```

They authorize the containing action (Update against the stored record), take the same bounds as lookups, and return only `{ id, label }` of live accounts.

## Sign-in

With `Identity`, the closed endpoint set is `POST /_identity/login`, `POST /_identity/logout`, `GET /_identity/session`, and `POST /_identity/register` (only with `Registration Enabled`).

- Sessions are cookie-only. The account ID is in the `bever_identity_id` claim. Cookies are `HttpOnly`, `SameSite=Lax`, and `Secure` outside development.
- Request bodies are closed JSON. Errors use `{ "error": { "code", "message" } }`: `ValidationFailed` (400), `InvalidCredentials` (401, the same for every failed login), `Unauthenticated` (401), `Conflict` (409, email already registered).
- Logout is idempotent `204`. Session returns `authenticated: false` for anonymous or stale cookies.
- A session is `{ authenticated, identity, administrator, roles }`. `roles` lists the caller's assigned roles that the model declares, in declaration order; assignments to undeclared roles allow nothing and are left out.
- Passwords need at least 8 characters and are hashed with ASP.NET Core's `PasswordHasher`.

### Accounts

When the model declares at least one `Role`, Administrators manage role assignments over HTTP:

```text
GET   /_identity/accounts?search=<text>&limit=<n>&cursor=<token>
PATCH /_identity/accounts/{id}     { "roles": ["Agent"] }
```

- Both answer `401` without a live identity and `403` to anyone but an Administrator, before reading the request.
- The list returns `{ items: [{ id, label, administrator, roles }], nextCursor }` of live accounts, ordered and paged like the account pickers, with the same bounds. `picker` and unknown parameters are `400`.
- `PATCH` replaces the account's roles with a closed body naming declared roles exactly, each once; anything else is `400 ValidationFailed`. An unknown or inactive account is `404`. The response is the updated account.
- Assignments and revocations are audited with the acting Administrator. Administrators and grants have no HTTP surface, so these routes cannot widen who is an Administrator.

## Antiforgery

Every state-changing request (`POST`, `PATCH`, `DELETE`, and sign-in, sign-out, and registration) is validated with ASP.NET Core antiforgery, signed in or not. Reads, lookups, and hydration are not. Issuing tokens is the host's job: the template maps `GET /api/v1/_antiforgery/token`, and its client fetches a fresh token before each change so the token stays bound to the current sign-in.

## Startup

- The model loader accepts exactly `BeverModel.ModelVersion` and rejects duplicate JSON properties; a mismatch names both versions and says to recompile.
- Application schema: when the SQLite schema is not current, startup logs a critical message and continues. The runtime never creates or changes the application schema.
- Identity, outside development: startup fails if the account tables are not current, if cookies would not be `Secure`, or if the identity store is not SQLite-backed. The runtime never creates or changes the account tables.

## Storage

`AddBever()` registers `SqliteBeverDatabaseProvider` at `BeverOptions.DatabasePath` (default `../.bever/bever.db` from the content root) unless another `IBeverDatabaseProvider` is registered first. Deletes are soft: records get `DeletedOn` and `DeletedBy`, disappear from every route, and trigger `OnDelete` (`Restrict` answers `409 DeleteRestricted`). See [migrations.md](migrations.md) for the schema.
