# Contributing to Bever

Thank you for helping. This repository holds the compiler (`Bever.Core`), the semantic model (`Bever.Model`), migrations, SQLite storage, and the ASP.NET Core runtime.

## Before you start

- For bugs, open an issue with a minimal `.bev` model, what you expected, and what happened.
- For language or contract changes (new syntax, new model properties, new HTTP routes, changed defaults), open an issue first. These changes touch several repositories, so agree the design in the issue before opening a pull request.
- Small fixes and documentation improvements can go straight to a pull request.
- [docs/architecture.md](docs/architecture.md), [docs/runtime.md](docs/runtime.md), and [docs/migrations.md](docs/migrations.md) describe the rules the code follows. Read the one that covers your change.

## Development

```sh
dotnet test
```

Tests run without network access or external services. `tests/` mirrors `src/`:

| Tests | Cover |
| --- | --- |
| `Bever.Core.Tests` | Parsing, diagnostics, and model output, including JSON contract fixtures |
| `Bever.Migrations.Tests` | Schema projection, diffing, risk classes, and hashes |
| `Bever.Sqlite.Tests` | Storage, migrations, delete behavior, and accounts |
| `Bever.AspNetCore.Tests` | HTTP routes, validation, authorization, lookups, and sign-in |

## Guidelines

- **Boundaries.** `Bever.Model` contains contract types only. `Bever.Core` depends only on `Bever.Model`. `Bever.AspNetCore` never depends on `Bever.Core`: runtimes consume the compiled model. A test in `Bever.AspNetCore.Tests` guards this.
- **Contract changes.** A change to the semantic model JSON bumps `BeverModel.ModelVersion`, updates the fixtures in `tests/Bever.Core.Tests/fixtures`, and needs matching changes in [bever-js](https://github.com/bever-team/bever-js) and the [reference documentation](https://github.com/bever-team/bever.dev).
- **Diagnostics.** Every compiler error has a stable code, a source location, and a message that says how to fix it. Unsupported constructs are errors, never silently ignored.
- **Database changes.** Changes to schema projection, migrations, or SQLite need tests for hash stability, backfills, and delete behavior.
- **Style.** Follow `.editorconfig`. Warnings are errors. Keep comments for the *why*.

## Pull requests

- Keep each pull request focused on one change and include tests.
- Describe the change, why it is needed, and how you verified it.
- `dotnet test` must pass.

By participating you agree to follow the [code of conduct](https://github.com/bever-team/.github/blob/main/CODE_OF_CONDUCT.md).
