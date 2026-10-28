using System.Text;

namespace Bever.AspNetCore;

public static class EntityRouteName
{
    public static string FromEntityName(string entityName) => Pluralize(ToKebabCase(entityName));

    private static string ToKebabCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsWhiteSpace(current) || current == '_' || current == '-')
            {
                AppendDash(builder);
                continue;
            }

            if (char.IsUpper(current) && builder.Length > 0 && builder[^1] != '-')
            {
                var previous = value[index - 1];
                var hasNextLower = index + 1 < value.Length && char.IsLower(value[index + 1]);
                if (char.IsLower(previous) || char.IsDigit(previous) || hasNextLower)
                {
                    builder.Append('-');
                }
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString().Trim('-');
    }

    private static void AppendDash(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '-')
        {
            builder.Append('-');
        }
    }

    private static string Pluralize(string value)
    {
        if (value.EndsWith('y') && value.Length > 1 && !IsVowel(value[^2]))
        {
            return value[..^1] + "ies";
        }

        if (value.EndsWith('s') || value.EndsWith('x') || value.EndsWith('z') || value.EndsWith("ch", StringComparison.Ordinal) || value.EndsWith("sh", StringComparison.Ordinal))
        {
            return value + "es";
        }

        return value + "s";
    }

    private static bool IsVowel(char value) => value is 'a' or 'e' or 'i' or 'o' or 'u';
}
