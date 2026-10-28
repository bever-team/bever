# Bever.Migrations

Provider-neutral database planning for [Bever](https://bever.dev) applications: it projects a semantic model to a logical persistence schema, diffs schemas into risk-classified operations, and computes canonical hashes for snapshots and migration files.

Applications normally use it through `bever migration add`, `bever migration apply`, and `bever dev`. See the [migrations guide](https://bever.dev/guide/migrations).
