# Bever.Core

The [Bever](https://bever.dev) compiler. It parses `.bev` sources, reports source-located diagnostics, and builds the semantic model defined by `Bever.Model`.

> **Bever is pre-alpha.** The language and APIs will change in breaking ways between releases, and there is no upgrade path yet. Do not use it for production data. See [Status](https://bever.dev/guide/what-is-bever#status).

```csharp
using Bever.Core;

var result = new BeverCompiler().Compile([SourceText.FromFile("bev/app.bev")]);
foreach (var diagnostic in result.Diagnostics)
{
    Console.Error.WriteLine(diagnostic);
}

if (result.Success)
{
    Console.WriteLine($"Compiled {result.Model.Entities.Count} entities.");
}
```

Most projects use the compiler through the `bever` CLI (`dotnet tool install --global --prerelease Bever.Cli`). See the [language reference](https://bever.dev/reference/language).
