# Bever.AspNetCore

Serve a [Bever](https://bever.dev) application from ASP.NET Core. The library loads the compiled semantic model and maps a validated, authorized HTTP API: CRUD routes for each declared action, relationship lookups, and cookie-based sign-in when the model declares `Identity`.

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
