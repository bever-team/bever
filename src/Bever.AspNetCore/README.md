# Bever.AspNetCore

Serve a [Bever](https://bever.dev) application from ASP.NET Core. The library loads the compiled semantic model and maps a validated, authorized HTTP API: CRUD routes for each declared action, relationship lookups, and cookie-based sign-in when the model declares `Identity`.

> **Bever is pre-alpha.** The language and APIs will change in breaking ways between releases, and there is no upgrade path yet. Do not use it for production data. See [Status](https://bever.dev/guide/what-is-bever#status).

```csharp
using Bever.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBever(options => options.ModelPath = "../bev/.output/model.json");

var app = builder.Build();
app.UseAuthentication();
app.MapBeverApi();
app.Run();
```

New projects created with `bever new` are already wired up. See the [HTTP API reference](https://bever.dev/reference/http-api).
