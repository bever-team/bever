# Architecture

This document is for contributors. The user documentation lives at [bever.dev](https://bever.dev); its [language](https://bever.dev/reference/language), [semantic model](https://bever.dev/reference/semantic-model), and [HTTP API](https://bever.dev/reference/http-api) references are the public contract. Two companion documents go deeper:

- [runtime.md](runtime.md): how `Bever.AspNetCore` authorizes, looks up, labels, and signs in.
- [migrations.md](migrations.md): schema projection, migration artifacts, hashes, and SQLite storage.

## Principles

1. **Business meaning first.** `.bev` files describe what the business needs: entities, fields, relationships, actions, and who may perform them. How it is rendered, stored, or deployed lives in libraries and in the project's own code.
2. **Compact and predictable.** The language uses few tokens and regular patterns, so people and coding agents can read and write it reliably.
3. **Secure by default.** Nothing is allowed until an action allows it, and every action declares `By` rules.
4. **What compiles is what runs.** Unknown and planned constructs are errors, never silently dropped.
5. **Projects own their code.** `bever new` creates a normal client and server that reference Bever packages. Generated application source is not the architecture; the semantic model is.
6. **Implementation agnostic.** The language does not assume a UI framework, database, or hosting stack. The default stack (ASP.NET Core, SQLite, Nuxt) is one implementation of the contract.

## The pipeline

```text
bev/*.bev ──▶ Bever.Core ──▶ semantic model (JSON) ──▶ Bever.AspNetCore (server)
                                                    └──▶ @bever/sdk (client, tools)
```

The **semantic model** is the only artifact that crosses component boundaries. The compiler produces it; runtimes and SDKs consume it and never parse `.bev` source. It is versioned (`semanticModelVersion`), and consumers accept exactly the version they were built for.

## Components

### Bever.Model

Contract types for the semantic model and `BeverValueFormats`, the shared rules for the text formats of primitive values (Email, Url, Date, DateTime, ...). It has no behavior beyond those rules and no dependencies. No parsing, validation, HTTP, database, or UI code belongs here.

### Bever.Core

The compiler. `BeverParser` turns each source file into a syntax tree of declarations (`Import`, `Identity`, `Role`, `Entity`, `Action`) and reports structural errors with source spans. `SemanticModelBuilder` resolves names and types, validates fields, defaults, relationships, actions, and authorization, and builds the model. `PresentationSemanticBuilder` does the same for `Present` blocks. A compilation with errors returns an empty model.

Every unsupported construct is an error with a code (`BEV0001`...). Planned keywords are reserved and reported as *not supported yet*. The codes are listed in the [diagnostics reference](https://bever.dev/reference/diagnostics).

### Bever.Migrations

Projects a model to a provider-neutral `PersistenceSchema` (entities, fields, system fields), diffs two schemas into risk-classified operations (`Safe`, `RequiresBackfill`, `Destructive`), and computes canonical SHA-256 hashes. Snapshots and migration files are JSON and independent of formatting and providers. See [migrations.md](migrations.md).

### Bever.Sqlite

Applies migrations to a SQLite file (table rebuilds for field changes), stores records with soft delete and system fields, enforces `OnDelete` behavior, and owns the private account tables (`__bever_identit*`): accounts with ASP.NET Core password hashes, Administrators, grants, role assignments, and an audit log. Account tables are not part of application migrations or schema hashes.

### Bever.AspNetCore

`AddBever()` registers the runtime; `MapBeverApi()` maps routes from the model's actions. For each request the runtime:

1. validates the antiforgery token for changes,
2. normalizes the JSON body (defaults on create, canonical value formats) and validates it against the entity's fields,
3. authorizes with the action's `By` rules, role assignments, Administrators, and grants (`BeverAuthorizationEvaluator`); owner rules on `Read` and `Pick` become a row scope that filters lists and lookups,
4. checks submitted references against each field's picker (`BeverReferences`),
5. calls the `IBeverDatabaseProvider` and adds `_labels` to returned records.

Startup validation (`BeverStartupValidator`) logs when the application schema is behind and, outside development, refuses to start with identity when the account tables are not current or cookies would not be `Secure`. See [runtime.md](runtime.md).

## Boundaries

- `.bev` describes business semantics. `Bever.Core` parses and validates it and builds the model.
- `Bever.Model` is contract-only. `Bever.Core` depends only on `Bever.Model`. `Bever.AspNetCore` never depends on `Bever.Core`; a test in `Bever.AspNetCore.Tests` guards this.
- The server is the authority for validation, authorization, references, and persistence. Clients may hide controls they expect to fail, but never decide.
- Client code is owned and customized by the project. The SDK resolves presentation defaults; the server ignores presentation.
- Providers customize infrastructure, never language semantics.

## Presentation is client metadata

`Present` is a closed vocabulary of display hints. It can change labels, field projections, interaction modes, picker style, and the label, emphasis, confirmation, placement, order, and visibility of the declared CRUD actions. It cannot create an action, route, payload, permission, validation rule, search field, or stored value.

- The server never reads presentation to decide routes, validation, or authorization. `Mode None` and `Hidden true` suppress generated client controls and pages, not API capabilities.
- `Update` and `Delete` are valid without `Read`. The server loads the stored record internally to authorize and execute a mutation without exposing it.
- Picker styles (`Dropdown`, `Combobox`, `Modal`) choose display only. Search fields, ordering, and permissions come from the field's picker action.
- Presentation-only changes never need a migration.
- The model stores only what the source says. `resolveEntityPresentation` in [bever-js](https://github.com/bever-team/bever-js) is the reference implementation of the defaults, and the [presentation guide](https://bever.dev/guide/presentation) documents them.

The rules the generated client follows on top of this are in [bever-templates/docs/generated-client.md](https://github.com/bever-team/bever-templates/blob/main/docs/generated-client.md).

## Changing the semantic model

- **Structured, never raw.** Every construct the compiler accepts becomes explicit JSON properties. No property holds a `.bev` fragment that consumers would have to tokenize; paths are `{ segments }`, not strings. If the model cannot represent a construct yet, the compiler reports an error.
- **Every property is emitted.** Nullable properties appear as `null`, lists as arrays. Consumers rely on presence; external ingestion rejects missing, unknown, and duplicate properties.
- **Sparse, not resolved.** Presentation stores what the source says; computed defaults are never written into the model.
- **Source spans** (`sourceSpan`, 1-based line and column, path relative to the project) are carried by declarations for tools. Runtimes ignore them.
- **One version, one change.** A contract change bumps `BeverModel.ModelVersion` and updates, in the same release: the C# types and fixtures in `tests/Bever.Core.Tests/fixtures`, the SDK types and fixtures in bever-js, the template client fixture, and the [semantic model reference](https://bever.dev/reference/semantic-model). Older JSON is not migrated; projects recompile their `.bev` sources with the matching CLI.

## Exposure

The canonical model is not automatically public. The runtime serves it only when the host calls `MapBeverMetadata()`. The default template bundles the model into its client, which is acceptable because the model contains no secrets: no credentials, sessions, grants, role assignments, or Administrator assignments are ever part of the model. Role names are. A future public projection of the model would need its own design.

## Providers

`IBeverDatabaseProvider` is the storage seam: list, get, create, patch, delete, lookup, and hydrate. SQLite is the default; registering another provider before `AddBever()` replaces it. Providers receive authorization results (for example a `BeverRowScope`) and must apply them. They decide how and where data is stored, never whether a request is allowed or what a value means: a provider must not bypass authorization, change validation, or skip `OnDelete` behavior.

## Related repositories

- [bever-cli](https://github.com/bever-team/bever-cli) uses `Bever.Core`, `Bever.Migrations`, and `Bever.Sqlite` for `bever compile`, `bever dev`, migrations, and account management.
- [bever-js](https://github.com/bever-team/bever-js) mirrors the model types and owns the presentation defaults (`resolveEntityPresentation`).
- [bever-templates](https://github.com/bever-team/bever-templates) is the project template: a Nuxt client and an ASP.NET Core server that reference these packages.
- [bever-vscode](https://github.com/bever-team/bever-vscode) highlights `.bev` files and shows `bever check` diagnostics.
- [bever.dev](https://github.com/bever-team/bever.dev) is the website and the public reference.

Versions are released in lockstep: CLI, packages, SDK, and templates share a version number. The CLI bundles the templates of its version and writes that version into new projects, so a project's packages match the CLI that created it.
