using System.Globalization;
using Bever.Model;

namespace Bever.Migrations;

public static class MigrationLiteralParser
{
    public static MigrationLiteral Parse(string fieldPath, PersistenceField field, string value)
    {
        if (field.Kind == "Enum")
        {
            if (!field.EnumValues.Contains(value, StringComparer.Ordinal))
            {
                throw new MigrationException($"Backfill for '{fieldPath}' must be one of: {string.Join(", ", field.EnumValues)}.");
            }

            return new MigrationLiteral { Kind = "Text", Value = value };
        }

        if (field.Kind == "Relationship")
        {
            return new MigrationLiteral { Kind = "Text", Value = value };
        }

        return field.TypeName switch
        {
            "Boolean" when bool.TryParse(value, out var boolean) => new MigrationLiteral { Kind = "Boolean", Value = boolean },
            "Boolean" => throw new MigrationException($"Backfill for '{fieldPath}' must be True or False."),
            "Int" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) =>
                new MigrationLiteral { Kind = "Integer", Value = integer },
            "Int" => throw new MigrationException($"Backfill for '{fieldPath}' must be an integer."),
            "Decimal" or "Money" when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) =>
                new MigrationLiteral { Kind = "Decimal", Value = number },
            "Decimal" or "Money" => throw new MigrationException($"Backfill for '{fieldPath}' must be a decimal number."),
            { } typeName when !BeverValueFormats.IsValid(typeName, value) =>
                throw new MigrationException($"Backfill for '{fieldPath}' is not a valid {typeName}. {BeverValueFormats.Describe(typeName)}"),
            { } typeName => new MigrationLiteral { Kind = "Text", Value = BeverValueFormats.Normalize(typeName, value) },
            _ => new MigrationLiteral { Kind = "Text", Value = value }
        };
    }
}
