# Migrations and storage

How a model becomes a database schema, and how schema changes are planned, recorded, and applied. The user guide is [Database migrations](https://bever.dev/guide/migrations); this document records the contract that `Bever.Migrations`, `Bever.Sqlite`, and the CLI share.

## Artifacts

A project keeps provider-neutral JSON next to its sources and commits it:

```text
bev/
  schema.snapshot.json      the last accepted persistence schema
  migrations/*.json         ordered changes between accepted schemas
  .output/model.json        the compiled semantic model (generated, not history)
```

- `PersistenceSchemaProjector` projects a semantic model to a `PersistenceSchema` (format version `1`): entities, their fields, and the system fields.
- A migration (`MigrationArtifact`, format version `1`) has an `id` (`<yyyyMMddHHmmss>_<name>`), a `name`, `fromSchemaHash`, `toSchemaHash`, and `operations`.
- Unknown format versions and operation kinds are rejected when reading.

## Persistence schema

What is stored, and what is not:

| In the schema | Not in the schema |
| --- | --- |
| entity and field names | presentation |
| field kind and type name, nullability | actions, `By` rules, pickers, `SearchBy`, `OrderBy` |
| `Text(n)` length | defaults, entity `Rule`s |
| enum option names (not their integer values) | enum option order |
| relationship target, target kind, `OnDelete` | `Identity` declaration and account tables |

Changing anything in the right column never needs a migration. Declaring `Identity` alone does not change the schema; a `-> Identity` field is an ordinary text field holding an account ID, with no foreign key to the account tables.

Every entity has the system fields `Id` (required text), `CreatedOn` and `ModifiedOn` (required timestamps), `CreatedBy` and `ModifiedBy` (nullable account IDs), and `DeletedOn` and `DeletedBy` (nullable, soft delete). Actor fields are nullable so anonymous and system writes need no fabricated account.

## Canonical hashes

`SchemaJson.Hash` is the lowercase hex SHA-256 of the schema's canonical JSON: entities ordered by name, fields by name, enum options by name, camelCase properties, nulls omitted, no whitespace. The hash is therefore independent of formatting, declaration order, file paths, timestamps, and provider. Migration operations are canonicalized the same way (ordered by entity, field, kind).

Hashes chain migrations: each migration starts at the hash the previous one ended with. Applying verifies the chain and the live database's recorded hash; a mismatch is an error, never ignored. Two branches that both add a migration produce *Migration history branches* when merged.

## Operations and risk

`SchemaDiffer` compares two schemas by name. Names are identity: a renamed entity or field is a drop plus an add. Explicit renames are on the roadmap.

| Operation | Risk |
| --- | --- |
| `CreateEntity` | `Safe` |
| `DropEntity`, `DropField` | `Destructive` |
| `AddField`, optional | `Safe` |
| `AddField`, required | `RequiresBackfill` |
| `AlterField`, required → optional | `Safe` |
| `AlterField`, optional → required | `RequiresBackfill` |
| `AlterField`, longer or removed text limit, added enum options, changed `OnDelete` | `Safe` |
| `AlterField`, any other change (kind, type, shorter limit, removed option, other target) | `Destructive` |

- `RequiresBackfill` needs a literal per field (`--backfill Entity.Field=value`), validated against the field type and stored as a structured `{ kind, value }` in the migration. Expressions, queries, callbacks, and provider SQL are not supported and do not belong in provider-neutral migrations.
- `Destructive` operations are never implicit: `bever migration add` needs `--allow-destructive`, and `bever dev` asks before applying.

## Lifecycle

- **Development.** `bever dev` creates the first migration and database on its first run, then shows the plan and asks before creating and applying each later migration.
- **Production.** Nothing is created or applied implicitly. `bever migration apply` is an explicit deployment step; it applies each migration in a transaction and records the resulting hash. The server starts with a database that is behind and logs a critical message.
- **Accounts.** The account tables have their own version, applied by `bever migration apply` or `bever identity schema apply`. Status is `Uninitialized`, `Current`, `SchemaMismatch`, or `SchemaDrift`, the same states as the application schema. Outside development the server refuses to start with identity when they are not current. Nothing ever drops account data implicitly.

## SQLite

All entities share one database file (default `.bever/bever.db`). Tables and columns use the quoted entity and field names, not HTTP route names.

| Bever type | SQLite |
| --- | --- |
| `Id`, text types, enums, relationships, dates and times | `TEXT` |
| `Boolean` | `INTEGER` with `CHECK (x IN (0, 1))` |
| `Int` | `INTEGER` |
| `Decimal`, `Money` | invariant decimal `TEXT`, never `REAL`, so values are exact |

- Text limits and enum options are `CHECK` constraints; enums store the option name.
- Relationships to entities are foreign keys with `ON DELETE RESTRICT | CASCADE | SET NULL`, and soft deletes apply the same behavior. Relationships to `Identity` have no foreign key.
- Field changes rebuild the table, so drops, alterations, and backfills behave the same on every SQLite version.
- Provider tables: `__bever_schema` (current schema JSON and hash) and `__bever_migrations` (applied history).
- Account tables (`__bever_identity_schema`, `__bever_identities`, `__bever_identity_credentials`, `__bever_identity_administrators`, `__bever_identity_grants`, `__bever_identity_roles`, `__bever_identity_audit`) are private to the provider. They are not generated from `.bev`, never appear in snapshots or migrations, and are excluded from schema hashes.

## Changing this contract

Changes to projection, diffing, hashing, or SQLite storage need tests for hash stability, risk classes, backfills, and delete behavior (`Bever.Migrations.Tests`, `Bever.Sqlite.Tests`). A change that alters existing hashes or artifact shapes breaks every project's migration chain; it needs an agreed design and a format version bump.
