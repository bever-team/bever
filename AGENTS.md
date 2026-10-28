# AGENTS.md

Guidance for coding agents working in this repository. Humans: see [CONTRIBUTING.md](CONTRIBUTING.md).

## What this is

The Bever compiler and .NET runtime: `.bev` sources compile to a versioned JSON semantic model, which the ASP.NET Core runtime serves as a validated, authorized HTTP API.

| Project | Role |
| --- | --- |
| `src/Bever.Model` | Semantic model contract types and value formats. No behavior, no dependencies. |
| `src/Bever.Core` | Compiler: parser, semantic and presentation builders, diagnostics (`BEV....`). |
| `src/Bever.Migrations` | Schema projection, diffing, risk classes, canonical hashes. |
| `src/Bever.Sqlite` | SQLite storage, migrations, private account tables. |
| `src/Bever.AspNetCore` | Runtime: routes, validation, authorization, lookups, sign-in. |
| `tests/*` | One test project per source project; `tests/Bever.Core.Tests/fixtures` holds the model contract fixtures. |

## Read first

Read these instead of scanning the code; they are the source of truth. When code and docs disagree, say so rather than silently following either.

| Topic | Document |
| --- | --- |
| Architecture, principles, boundaries, changing the model | [docs/architecture.md](docs/architecture.md) |
| Runtime rules: authorization, row scope, lookups, sign-in | [docs/runtime.md](docs/runtime.md) |
| Migrations, hashes, SQLite storage | [docs/migrations.md](docs/migrations.md) |
| Language | [bever.dev/reference/language](https://bever.dev/reference/language) (`../bever.dev/docs/reference/language.md` when checked out alongside) |
| Semantic model JSON | [bever.dev/reference/semantic-model](https://bever.dev/reference/semantic-model) |
| HTTP API | [bever.dev/reference/http-api](https://bever.dev/reference/http-api) |
| Diagnostics | [bever.dev/reference/diagnostics](https://bever.dev/reference/diagnostics) |
| Planned features | [bever.dev/roadmap](https://bever.dev/roadmap) |

## Commands

```sh
dotnet test                      # all tests; no network or external services
dotnet test tests/Bever.Core.Tests
dotnet pack -c Release -o artifacts/packages
```

## Rules

- **Dependencies.** `Bever.Model` depends on nothing. `Bever.Core` depends only on `Bever.Model`. `Bever.AspNetCore` never depends on `Bever.Core`. Providers never redefine language semantics.
- **Only implemented constructs compile.** Unknown or planned syntax is an error with a code and a source span, never ignored or stored as a raw string. Do not add planned features from the roadmap without an agreed design.
- **The model is structured.** No `.bev` fragments in JSON; every property emitted, `null` when empty; presentation stored sparse.
- **The server decides.** Presentation never affects routes, validation, or authorization.
- **Contract changes are coordinated.** A semantic model change bumps `BeverModel.ModelVersion`, updates `tests/Bever.Core.Tests/fixtures`, and needs matching changes in bever-js (types, fixtures), bever-templates (client fixture), and bever.dev (reference). Mention the other repositories in your summary.
- **Design first.** Changes to syntax, defaults, validation meaning, model shape or version, HTTP routes or payloads, authorization, migration artifacts or hashes, risk classes, provider interfaces, or package dependencies need a design agreed with a maintainer, usually in an issue, before implementation.
- **Docs ship with code.** Update `docs/` here and the bever.dev reference in the same change. Document only what is implemented.
- **Database changes** need tests for hash stability, risk classes, backfills, and delete behavior.
- **Style.** Follow `.editorconfig`; warnings are errors. Comments explain why. Mark deliberate simplifications with a `Limitation:` comment naming the ceiling.
- **Test data** uses neutral names (Ada, Bob) and `example.com` addresses.
