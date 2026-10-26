#nullable enable

using System;

namespace Orleans.Serialization;

// Shared with the attribute analyzer so declarations have the same contract at build time and runtime.
internal static class IdentityNameValidator
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is '"' or '\'' || char.IsWhiteSpace(character) || char.IsControl(character))
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
            throw new ArgumentException("An Orleans identity must be non-empty and contain no quotes, whitespace, or control characters.", parameterName);
        }
    }
}
