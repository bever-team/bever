# Bever.Migrations

Provider-neutral database planning for [Bever](https://bever.dev) applications: it projects a semantic model to a logical persistence schema, diffs schemas into risk-classified operations, and computes canonical hashes for snapshots and migration files.

> **Bever is pre-alpha.** The language and APIs will change in breaking ways between releases, and there is no upgrade path yet. Do not use it for production data. See [Status](https://bever.dev/guide/what-is-bever#status).

Applications normally use it through `bever migration add`, `bever migration apply`, and `bever dev`. See the [migrations guide](https://bever.dev/guide/migrations).
