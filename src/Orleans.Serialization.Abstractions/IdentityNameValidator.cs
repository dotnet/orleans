#nullable enable

using System;

namespace Orleans.Serialization;

// Shared with the attribute analyzer so declarations have the same contract at build time and runtime.
internal static class IdentityNameValidator
{
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is '"' or '\'' or '[' or ']' or '(' or ')' or ',' or '*' or '&'
                || char.IsWhiteSpace(character)
                || char.IsControl(character))
            {
                return false;
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
            throw new ArgumentException("An Orleans identity must be non-empty and contain no quotes, whitespace, control characters, or reserved type-name delimiters ([](),*&).", parameterName);
        }
    }
}
