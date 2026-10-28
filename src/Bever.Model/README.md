# Bever.Model

The Bever semantic model: the versioned JSON contract that the [Bever](https://bever.dev) compiler produces and that runtimes, SDKs, and tools consume.

```csharp
using System.Text.Json;
using Bever.Model;

var model = JsonSerializer.Deserialize<BeverModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Console.WriteLine($"{model!.Entities.Count} entities, model {model.SemanticModelVersion}");
```

The package contains contract types only. It also defines `BeverValueFormats`, the shared rules for what a valid Email, Url, Date, Time, or DateTime value looks like.

See the [semantic model reference](https://bever.dev/reference/semantic-model) for the full contract.
