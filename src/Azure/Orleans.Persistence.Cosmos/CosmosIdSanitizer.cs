// <copyright file="CosmosIdSanitizer.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Sanitizes and unsanitizes Cosmos DB IDs.
/// </summary>
public static class CosmosIdSanitizer
{
    /// <summary>
    /// The separator character, used to separate elements in a compound string.
    /// </summary>
    public const char SeparatorChar = '_';

    private const char EscapeChar = '~';

    private static ReadOnlySpan<char> SanitizedCharacters => new[] { '/', '\\', '?', '#', SeparatorChar, EscapeChar };

    private static ReadOnlySpan<char> ReplacementCharacters => new[] { '0', '1', '2', '3', '4', '5' };

    /// <summary>
    /// Sanitizes the provided value, escaping any characters that are not allowed in Cosmos DB IDs.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns>The sanitized value.</returns>
    public static string Sanitize(string input)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var count = 0;
        foreach (var c in input)
        {
            var charId = SanitizedCharacters.IndexOf(c);
            if (charId >= 0)
            {
                ++count;
            }
        }

        if (count == 0)
        {
            return input;
        }

        return string.Create(input.Length + count, input, static (output, input) =>
        {
            var i = 0;
            foreach (var c in input)
            {
                var charId = SanitizedCharacters.IndexOf(c);
                if (charId < 0)
                {
                    output[i++] = c;
                    continue;
                }

                output[i++] = EscapeChar;
                output[i++] = ReplacementCharacters[charId];
            }
        });
    }

    /// <summary>
    /// Reverses the result of <see cref="Sanitize(string)"/>, yielding the original string.
    /// </summary>
    /// <param name="input">The sanitized value.</param>
    /// <returns>The unsanitized value.</returns>
    /// <exception cref="ArgumentException">The value is not a valid sanitized value.</exception>
    public static string Unsanitize(string input)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var count = 0;
        foreach (var c in input)
        {
            if (c == EscapeChar)
            {
                ++count;
            }
        }

        if (count == 0)
        {
            return input;
        }

        return string.Create(input.Length - count, input, static (output, input) =>
        {
            var i = 0;
            var isEscaped = false;
            foreach (var c in input)
            {
                if (isEscaped)
                {
                    var charId = ReplacementCharacters.IndexOf(c);
                    if (charId < 0)
                    {
                        throw new ArgumentException($"Input is not in a valid format: Encountered unsupported escape sequence");
                    }

                    output[i++] = SanitizedCharacters[charId];
                    isEscaped = false;
                }
                else if (c == EscapeChar)
                {
                    isEscaped = true;
                }
                else
                {
                    output[i++] = c;
                }
            }
        });
    }
}