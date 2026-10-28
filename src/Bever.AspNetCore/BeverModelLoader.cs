using System.Text.Json;
using System.Text.Json.Serialization;
using Bever.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bever.AspNetCore;

public sealed class BeverModelLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly BeverOptions options;
    private readonly IHostEnvironment environment;

    public BeverModelLoader(IOptions<BeverOptions> options, IHostEnvironment environment)
    {
        this.options = options.Value;
        this.environment = environment;
    }

    public BeverModel Load()
    {
        var modelPath = Path.IsPathRooted(options.ModelPath)
            ? options.ModelPath
            : Path.Combine(environment.ContentRootPath, options.ModelPath);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Bever semantic model not found: {modelPath}", modelPath);
        }

        var json = File.ReadAllBytes(modelPath);
        RejectDuplicateProperties(json, modelPath);
        RejectOtherModelVersions(json, modelPath);
        RejectMissingRequiredNullableProperties(json, modelPath);
        BeverModel model;
        try
        {
            model = JsonSerializer.Deserialize<BeverModel>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Bever semantic model is empty: {modelPath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Invalid Bever semantic model JSON at {modelPath}{exception.Path}: {exception.Message}",
                exception);
        }

        BeverModelValidator.Validate(model, modelPath);
        return model;
    }

    // Checked before deserializing so an old or newer model reports its version, not an unknown property.
    private static void RejectOtherModelVersions(byte[] json, string modelPath)
    {
        using var document = JsonDocument.Parse(json);
        var version = TryGetPropertyCaseInsensitive(document.RootElement, "semanticModelVersion", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        if (version != BeverModel.ModelVersion)
        {
            throw new InvalidOperationException(
                $"Bever semantic model {modelPath} has version '{version}', but this runtime requires '{BeverModel.ModelVersion}'. Recompile the bev/ sources with a matching bever CLI.");
        }
    }

    private static void RejectDuplicateProperties(byte[] json, string modelPath)
    {
        using var document = JsonDocument.Parse(json);
        RejectDuplicateProperties(document.RootElement, "$", modelPath);
    }

    private static void RejectDuplicateProperties(JsonElement element, string jsonPath, string modelPath)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException($"Duplicate JSON property '{property.Name}' at {jsonPath} in {modelPath}.");
                }

                RejectDuplicateProperties(property.Value, $"{jsonPath}.{property.Name}", modelPath);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{jsonPath}[{index++}]", modelPath);
            }
        }
    }

    // Absent properties deserialize to null or empty defaults, so required ones are checked on the raw JSON.
    private static void RejectMissingRequiredNullableProperties(byte[] json, string modelPath)
    {
        using var document = JsonDocument.Parse(json);
        RequireProperties(document.RootElement, "$", modelPath, "identity", "roles");
        if (TryGetPropertyCaseInsensitive(document.RootElement, "actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var action in actions.EnumerateArray())
            {
                var actionPath = $"$.actions[{index++}]";
                RequireProperties(action, actionPath, modelPath, "searchBy", "name");
                if (action.ValueKind == JsonValueKind.Object && TryGetPropertyCaseInsensitive(action, "authorizations", out var authorizations) && authorizations.ValueKind == JsonValueKind.Array)
                {
                    var authorizationIndex = 0;
                    foreach (var authorization in authorizations.EnumerateArray())
                    {
                        RequireProperties(authorization, $"{actionPath}.authorizations[{authorizationIndex++}]", modelPath, "role");
                    }
                }
            }
        }

        if (TryGetPropertyCaseInsensitive(document.RootElement, "entities", out var entities) && entities.ValueKind == JsonValueKind.Array)
        {
            var entityIndex = 0;
            foreach (var entity in entities.EnumerateArray())
            {
                if (entity.ValueKind == JsonValueKind.Object && TryGetPropertyCaseInsensitive(entity, "fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
                {
                    var fieldIndex = 0;
                    foreach (var field in fields.EnumerateArray())
                    {
                        if (field.ValueKind == JsonValueKind.Object && TryGetPropertyCaseInsensitive(field, "type", out var type))
                        {
                            RequireProperties(type, $"$.entities[{entityIndex}].fields[{fieldIndex}].type", modelPath, "picker");
                        }

                        fieldIndex++;
                    }
                }

                entityIndex++;
            }
        }
    }

    private static void RequireProperties(JsonElement element, string jsonPath, string modelPath, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var name in names)
        {
            if (!TryGetPropertyCaseInsensitive(element, name, out _))
            {
                throw new InvalidOperationException($"Missing required JSON property '{name}' at {jsonPath} in {modelPath}.");
            }
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
