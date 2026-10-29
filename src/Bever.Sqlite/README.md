# Bever.Sqlite

SQLite storage for [Bever](https://bever.dev) applications. It applies Bever migration files, stores records with soft delete and audit fields, enforces relationship delete behavior, and owns the private identity schema (identities, password hashes, administrators, and grants).

> **Bever is pre-alpha.** The language and APIs will change in breaking ways between releases, and there is no upgrade path yet. Do not use it for production data. See [Status](https://bever.dev/guide/what-is-bever#status).

`Bever.AspNetCore` uses it by default. See the [deployment guide](https://bever.dev/guide/deployment).
