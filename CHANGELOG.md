# Changelog

## 0.1.0-prealpha.1

The first public pre-alpha release.

- Language 0.6: entities, typed fields with defaults, enums, relationships with delete behavior, validation rules (`Rule`), `Identity`, roles (`Role`), CRUD and `Pick` actions with `By` rules, `OrderBy`, `SearchBy`, `Present`, and `Export`/`Import` between files.
- Semantic model 0.10, consumed by the runtime and `@bever/sdk`.
- Source-located diagnostics for every error, including planned constructs that are not supported yet.
- ASP.NET Core runtime: CRUD routes, value and rule validation, owner-filtered lists, relationship lookups, cookie sign-in, antiforgery, roles, Administrators, and grants. Administrators assign roles over HTTP.
- SQLite storage with provider-neutral migrations, risk classes, backfills, and soft delete.
- Licensed under the Apache License 2.0.
