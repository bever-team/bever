<p align="center">
  <a href="https://bever.dev">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="https://bever.dev/brand/bever-logo-on-dark.svg">
      <img alt="Bever" src="https://bever.dev/brand/bever-logo.svg" width="240">
    </picture>
  </a>
</p>

<h3 align="center">Business logic as code.</h3>

<p align="center">
  <a href="https://bever.dev/guide/getting-started">Getting started</a> ·
  <a href="https://bever.dev/reference/language">Language</a> ·
  <a href="https://bever.dev/reference/http-api">HTTP API</a> ·
  <a href="https://bever.dev/roadmap">Roadmap</a>
</p>

---

Bever is an open-source language and framework for business applications. You describe entities, fields, relationships, and permissions in `.bev` files; Bever serves them as a validated, authorized HTTP API and a web app your team owns.

> **Bever is pre-alpha.** The language and APIs will change in breaking ways between releases, and there is no upgrade path yet. Do not use it for production data. See [Status](https://bever.dev/guide/what-is-bever#status).

```bev
Identity
    Registration Enabled

Entity Task
    Field Title Text(200)
    Field Status Enum(ToDo, Doing, Done) = ToDo
    Field Owner -> Identity

Action Create, Read Task
    By Authenticated

Action Update, Delete Task
    By Owner
```

This repository contains the compiler and the .NET runtime. To build an app, install the CLI and follow [Getting started](https://bever.dev/guide/getting-started):

```sh
dotnet tool install --global --prerelease Bever.Cli
bever new "My App"
```

## Packages

| Package | Description |
| --- | --- |
| [`Bever.Model`](src/Bever.Model) | The semantic model: the versioned JSON contract between the compiler and runtimes |
| [`Bever.Core`](src/Bever.Core) | The compiler: parses `.bev` sources, reports diagnostics, builds the model |
| [`Bever.Migrations`](src/Bever.Migrations) | Schema projection, migration planning, and canonical hashes |
| [`Bever.Sqlite`](src/Bever.Sqlite) | SQLite storage, migrations, and the private account tables |
| [`Bever.AspNetCore`](src/Bever.AspNetCore) | The ASP.NET Core runtime: CRUD, lookups, sign-in, and authorization |

## Related repositories

| Repository | Contents |
| --- | --- |
| [bever-cli](https://github.com/bever-team/bever-cli) | The `bever` command-line tool |
| [bever-js](https://github.com/bever-team/bever-js) | `@bever/sdk`, the JavaScript SDK |
| [bever-templates](https://github.com/bever-team/bever-templates) | Project templates used by `bever new` |
| [bever-vscode](https://github.com/bever-team/bever-vscode) | VS Code extension |
| [bever-examples](https://github.com/bever-team/bever-examples) | Example models |
| [bever.dev](https://github.com/bever-team/bever.dev) | Website and documentation |

## Build and test

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet test
dotnet pack -c Release -o artifacts/packages
```

[docs/architecture.md](docs/architecture.md) explains how the pieces fit together. See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request.

## Status

Bever is pre-1.0. The language and the semantic model may change between minor versions; the [roadmap](https://bever.dev/roadmap) lists what is planned.

## License

Licensed under the [Apache License 2.0](LICENSE). "Bever" and the Bever logo are trademarks; see [TRADEMARKS.md](TRADEMARKS.md).
