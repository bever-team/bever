using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bever.Model;

namespace Bever.AspNetCore;

internal sealed class BeverEntityValidator
{
    public BeverEntityValidationResult Validate(BeverEntityContext entity, JsonObject values, BeverEntityRequestOperation operation)
    {
        var errors = new List<BeverEntityValidationError>();
        var fieldsByRequestName = BuildFieldMap(entity);

        foreach (var property in values)
        {
            if (!fieldsByRequestName.TryGetValue(property.Key, out var field))
            {
                errors.Add(new BeverEntityValidationError(property.Key, "Field is not defined on this entity."));
                continue;
            }

            ValidateFieldValue(BeverEntityRequestNormalizer.GetRequestFieldName(field), field.Type, property.Value, errors);
        }

        if (operation == BeverEntityRequestOperation.Create)
        {
            foreach (var field in entity.Model.Fields)
            {
                var name = BeverEntityRequestNormalizer.GetRequestFieldName(field);
                if (!IsNullable(field.Type) && !values.ContainsKey(name))
                {
                    errors.Add(new BeverEntityValidationError(name, "Field is required."));
                }
            }
        }

        return new BeverEntityValidationResult(errors);
    }

    // Rules follow SQL CHECK logic: a comparison with an empty value is unknown, and only a false rule fails.
    // bool? '&', '|', and '!' are exactly that three-valued logic.
    public IReadOnlyList<BeverEntityValidationError> ValidateRules(BeverEntityContext entity, JsonObject record)
    {
        var errors = new List<BeverEntityValidationError>();
        foreach (var rule in entity.Model.Rules)
        {
            if (Evaluate(rule.Condition!, entity.Model, record) == false)
            {
                errors.Add(new BeverEntityValidationError(BeverEntityRequestNormalizer.GetRequestFieldName(FirstField(rule.Condition!, entity.Model)), rule.Message));
            }
        }

        return errors;
    }

    private static bool? Evaluate(ExpressionModel expression, EntityModel entity, JsonObject record) => expression.Kind switch
    {
        "And" => expression.Operands.Aggregate((bool?)true, (result, operand) => result & Evaluate(operand, entity, record)),
        "Or" => expression.Operands.Aggregate((bool?)false, (result, operand) => result | Evaluate(operand, entity, record)),
        "Not" => !Evaluate(expression.Operands[0], entity, record),
        "Compare" => Compare(expression, entity, record),
        "In" => In(expression, entity, record),
        _ => throw new InvalidOperationException($"Unsupported rule expression '{expression.Kind}'.")
    };

    private static bool? Compare(ExpressionModel expression, EntityModel entity, JsonObject record)
    {
        var field = FirstField(expression, entity);
        if (expression.Operands.Any(operand => operand.Kind == "Null"))
        {
            var isNull = ValueOf(expression.Operands.First(operand => operand.Kind == "Field"), field, entity, record) is null;
            return expression.Operator == "=" ? isNull : !isNull;
        }

        var left = ValueOf(expression.Operands[0], field, entity, record);
        var right = ValueOf(expression.Operands[1], field, entity, record);
        if (left is null || right is null)
        {
            return null;
        }

        var order = left is string leftText && right is string rightText ? string.CompareOrdinal(leftText, rightText) : left.CompareTo(right);
        return expression.Operator switch
        {
            "=" => order == 0,
            "!=" => order != 0,
            "<" => order < 0,
            "<=" => order <= 0,
            ">" => order > 0,
            ">=" => order >= 0,
            _ => throw new InvalidOperationException($"Unsupported rule operator '{expression.Operator}'.")
        };
    }

    private static bool? In(ExpressionModel expression, EntityModel entity, JsonObject record)
    {
        var field = FirstField(expression, entity);
        var value = ValueOf(expression.Operands[0], field, entity, record);
        return value is null ? null : expression.Operands.Skip(1).Any(option => Equals(value, ValueOf(option, field, entity, record)));
    }

    // A comparable value in the representation of 'field', the field the expression compares; null when empty.
    private static IComparable? ValueOf(ExpressionModel operand, FieldModel field, EntityModel entity, JsonObject record)
    {
        var text = operand.Kind switch
        {
            "Field" => record[BeverEntityRequestNormalizer.GetRequestFieldName(entity.Fields.First(candidate => candidate.Name == operand.Path!.Segments[0]))] switch
            {
                null => null,
                JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
                var node => node.ToJsonString()
            },
            "Literal" => operand.Literal!.Value switch
            {
                null => null,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                JsonElement element => element.GetRawText(),
                var value => Convert.ToString(value, CultureInfo.InvariantCulture)
            },
            "Today" => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "Now" => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unsupported rule value '{operand.Kind}'.")
        };

        return text is null ? null : field.Type!.Name switch
        {
            "Int" or "Decimal" or "Money" => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
            "Boolean" => bool.Parse(text),
            "Date" => DateOnly.Parse(text, CultureInfo.InvariantCulture),
            "Time" => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
            "DateTime" => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => text
        };
    }

    // Rule errors belong to the rule's first field, so forms show them next to that input.
    private static FieldModel FirstField(ExpressionModel expression, EntityModel entity) =>
        expression.Kind == "Field"
            ? entity.Fields.First(field => field.Name == expression.Path!.Segments[0])
            : expression.Operands.Select(operand => FirstFieldOrNull(operand, entity)).First(field => field is not null)!;

    private static FieldModel? FirstFieldOrNull(ExpressionModel expression, EntityModel entity) =>
        expression.Kind == "Field" || expression.Operands.Count > 0 ? FirstField(expression, entity) : null;

    private static Dictionary<string, FieldModel> BuildFieldMap(BeverEntityContext entity)
    {
        var fields = new Dictionary<string, FieldModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in entity.Model.Fields)
        {
            fields.TryAdd(field.Name, field);
            fields.TryAdd(BeverEntityRequestNormalizer.GetRequestFieldName(field), field);
        }

        return fields;
    }

    private static void ValidateFieldValue(string fieldName, FieldTypeModel? type, JsonNode? value, List<BeverEntityValidationError> errors)
    {
        if (type is null)
        {
            return;
        }

        if (value is null)
        {
            if (!type.Nullable)
            {
                errors.Add(new BeverEntityValidationError(fieldName, "Field cannot be null."));
            }

            return;
        }

        switch (type.Kind)
        {
            case "Primitive":
                ValidatePrimitive(fieldName, type, value, errors);
                break;
            case "Enum":
                ValidateEnum(fieldName, type, value, errors);
                break;
            case "Relationship":
                if (!IsString(value))
                {
                    errors.Add(new BeverEntityValidationError(fieldName, "Expected relationship id string."));
                }
                break;
        }
    }

    private static void ValidatePrimitive(string fieldName, FieldTypeModel type, JsonNode value, List<BeverEntityValidationError> errors)
    {
        switch (type.Name)
        {
            case "Text":
            case "Email":
            case "Phone":
            case "Url":
            case "Date":
            case "DateTime":
            case "Time":
                if (!TryGetValue<string>(value, out var text))
                {
                    errors.Add(new BeverEntityValidationError(fieldName, "Expected text."));
                    return;
                }

                if (!type.Nullable && string.IsNullOrWhiteSpace(text))
                {
                    errors.Add(new BeverEntityValidationError(fieldName, "Field is required."));
                    return;
                }

                var maxLength = GetIntArgument(type, "maxLength");
                if (maxLength is not null && text.Length > maxLength.Value)
                {
                    errors.Add(new BeverEntityValidationError(fieldName, $"Must be at most {maxLength.Value} characters."));
                }
                else if (!BeverValueFormats.IsValid(type.Name, text))
                {
                    errors.Add(new BeverEntityValidationError(fieldName, BeverValueFormats.Describe(type.Name)));
                }

                break;
            case "Boolean":
                if (!TryGetValue<bool>(value, out _))
                {
                    errors.Add(new BeverEntityValidationError(fieldName, "Expected true or false."));
                }

                break;
            case "Int":
                if (!TryGetValue<int>(value, out _))
                {
                    errors.Add(new BeverEntityValidationError(fieldName, "Expected a whole number."));
                }

                break;
            case "Decimal":
            case "Money":
                if (!TryGetValue<decimal>(value, out _))
                {
                    errors.Add(new BeverEntityValidationError(fieldName, "Expected a number."));
                }

                break;
        }
    }

    private static void ValidateEnum(string fieldName, FieldTypeModel type, JsonNode value, List<BeverEntityValidationError> errors)
    {
        foreach (var option in type.Options)
        {
            if (IsJsonValueEqual(value, option.Name) || (option.Value is not null && IsJsonValueEqual(value, option.Value)))
            {
                return;
            }
        }

        errors.Add(new BeverEntityValidationError(fieldName, "Expected one of the defined enum values."));
    }

    private static bool IsNullable(FieldTypeModel? type) => type?.Nullable == true;

    private static bool IsString(JsonNode value) => TryGetValue<string>(value, out _);

    private static bool TryGetValue<T>(JsonNode value, out T result)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue(out result!))
        {
            return true;
        }

        result = default!;
        return false;
    }

    private static int? GetIntArgument(FieldTypeModel type, string name)
    {
        foreach (var arguments in type.Arguments)
        {
            if (!arguments.TryGetValue(name, out var value) || value is null)
            {
                continue;
            }

            return value switch
            {
                int intValue => intValue,
                long longValue => checked((int)longValue),
                JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var intValue) => intValue,
                _ => null
            };
        }

        return null;
    }

    private static bool IsJsonValueEqual(JsonNode node, object expected)
    {
        if (expected is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => TryGetValue<string>(node, out var value) && value == element.GetString(),
                JsonValueKind.Number => TryGetValue<decimal>(node, out var value) && element.TryGetDecimal(out var expectedValue) && value == expectedValue,
                JsonValueKind.True => TryGetValue<bool>(node, out var value) && value,
                JsonValueKind.False => TryGetValue<bool>(node, out var value) && !value,
                _ => false
            };
        }

        return expected switch
        {
            string expectedValue => TryGetValue<string>(node, out var value) && value == expectedValue,
            int expectedValue => TryGetValue<int>(node, out var value) && value == expectedValue,
            long expectedValue => TryGetValue<long>(node, out var value) && value == expectedValue,
            bool expectedValue => TryGetValue<bool>(node, out var value) && value == expectedValue,
            _ => false
        };
    }
}

internal sealed record BeverEntityValidationResult(IReadOnlyList<BeverEntityValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

internal sealed record BeverEntityValidationError(string Field, string Message);
