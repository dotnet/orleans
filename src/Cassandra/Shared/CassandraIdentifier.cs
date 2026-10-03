using System;

namespace Orleans.Cassandra;

internal static class CassandraIdentifier
{
    private const int MaximumLength = 48;

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaximumLength
        && IsAsciiLetter(value[0])
        && AllIdentifierCharacters(value.AsSpan(1));

    public static string Quote(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("The value is not a valid Cassandra identifier.", nameof(value));
        }

        // Cassandra folds unquoted identifiers to lowercase. Preserve that behavior
        // while quoting the result so keywords remain valid identifiers.
        return $"\"{value.ToLowerInvariant().Replace("\"", "\"\"")}\"";
    }

    public static string Normalize(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("The value is not a valid Cassandra identifier.", nameof(value));
        }

        return value.ToLowerInvariant();
    }

    public static string QuoteLiteral(string value) =>
        $"'{(value ?? throw new ArgumentNullException(nameof(value))).Replace("'", "''")}'";

    private static bool AllIdentifierCharacters(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c != '_' && !IsAsciiLetter(c) && !IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
}
