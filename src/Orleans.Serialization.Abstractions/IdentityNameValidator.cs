#nullable enable

using System;

namespace Orleans.Serialization;

// Shared with the attribute analyzer so declarations have the same contract at build time and runtime.
internal static class IdentityNameValidator
{
    public const int MaxGenericArity = 64;

    public static bool IsValid(string? value)
    {
        if (value is null || value.Length == 0)
        {
            return false;
        }

        var totalArity = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '"' or '\'' or '[' or ']' or '(' or ')' or ',' or '*' or '&'
                || char.IsWhiteSpace(character)
                || char.IsControl(character))
            {
                return false;
            }

            if (character == '`')
            {
                var arityStart = index + 1;
                var arity = 0;
                while (index + 1 < value.Length && value[index + 1] is >= '0' and <= '9')
                {
                    arity = arity * 10 + value[++index] - '0';
                    if (arity > MaxGenericArity)
                    {
                        return false;
                    }
                }

                // Runtime type parsing consumes an arity suffix followed only by a nested type or the end.
                if (index < arityStart || index + 1 < value.Length && value[index + 1] != '+')
                {
                    return false;
                }

                totalArity += arity;
                if (totalArity > MaxGenericArity)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static void Validate(string value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (!IsValid(value))
        {
            throw new ArgumentException("An Orleans identity must be non-empty, contain no quotes, whitespace, control characters, or reserved type-name delimiters ([](),*&), and use decimal generic arity suffixes with a total arity of at most 64.", parameterName);
        }
    }
}
