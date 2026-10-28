# Bever.Sqlite

SQLite storage for [Bever](https://bever.dev) applications. It applies Bever migration files, stores records with soft delete and audit fields, enforces relationship delete behavior, and owns the private identity schema (identities, password hashes, administrators, and grants).

`Bever.AspNetCore` uses it by default. See the [deployment guide](https://bever.dev/guide/deployment).
