using System.Text.Json;
using System.Text.Json.Nodes;
using Bever.Model;

namespace Bever.AspNetCore;

internal sealed class BeverEntityRequestNormalizer
{
    public JsonObject Normalize(BeverEntityContext entity, JsonObject values, BeverEntityRequestOperation operation)
    {
        var fieldsByRequestName = BuildFieldMap(entity);
        var normalized = new JsonObject();

        foreach (var property in values)
        {
            var name = fieldsByRequestName.TryGetValue(property.Key, out var field)
                ? GetRequestFieldName(field)
                : property.Key;

            // Valid values get their canonical stored form (UTC date-times); invalid ones are left for the validator.
            normalized[name] = field?.Type is { Kind: "Primitive", Name: { } typeName } && property.Value is JsonValue value &&
                value.TryGetValue<string>(out var text) && BeverValueFormats.IsValid(typeName, text)
                ? JsonValue.Create(BeverValueFormats.Normalize(typeName, text))
                : property.Value?.DeepClone();
        }

        if (operation == BeverEntityRequestOperation.Create)
        {
            foreach (var field in entity.Model.Fields)
            {
                var name = GetRequestFieldName(field);
                if (!normalized.ContainsKey(name) && field.DefaultValue is not null)
                {
                    normalized[name] = CreateDefaultValue(field.DefaultValue);
                }
            }
        }

        return normalized;
    }

    public static string GetRequestFieldName(FieldModel field) => JsonNamingPolicy.CamelCase.ConvertName(field.Name);

    private static Dictionary<string, FieldModel> BuildFieldMap(BeverEntityContext entity)
    {
        var fields = new Dictionary<string, FieldModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in entity.Model.Fields)
        {
            fields.TryAdd(field.Name, field);
            fields.TryAdd(GetRequestFieldName(field), field);
        }

        return fields;
    }

    private static JsonNode? CreateDefaultValue(LiteralValueModel defaultValue) => defaultValue.Value switch
    {
        null => null,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        bool value => JsonValue.Create(value),
        int value => JsonValue.Create(value),
        long value => JsonValue.Create(value),
        decimal value => JsonValue.Create(value),
        double value => JsonValue.Create(value),
        string value => JsonValue.Create(value),
        _ => JsonValue.Create(defaultValue.Value.ToString())
    };
}

internal enum BeverEntityRequestOperation
{
    Create,
    Patch
}
