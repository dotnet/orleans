using System.Globalization;
using System.Text;

namespace Orleans.EventSourcing.Common;

/// <summary>
/// Encodes a set of replica write bits in a string.
/// </summary>
/// <remarks>
/// New values use the versioned format <c>v1:</c> followed by UTF-16 length-prefixed replica identifiers.
/// This representation supports every valid cluster identifier without delimiter restrictions. Legacy values
/// containing comma-prefixed tokens remain readable and are upgraded to the current format by <see cref="FlipBit"/>.
/// In legacy values, commas are interpreted as token delimiters because the previous format did not escape them.
/// Malformed or unsupported versioned values throw <see cref="FormatException"/>.
/// </remarks>
public static class StringEncodedWriteVector
{
    private const string CurrentFormatPrefix = "v1:";

    /// <summary>
    /// Gets one of the bits in <paramref name="writeVector"/>.
    /// </summary>
    /// <param name="writeVector">The write vector.</param>
    /// <param name="Replica">The replica whose bit is returned.</param>
    /// <returns><see langword="true"/> when the replica's bit is set.</returns>
    public static bool GetBit(string writeVector, string Replica)
    {
        ArgumentNullException.ThrowIfNull(writeVector);
        ArgumentException.ThrowIfNullOrEmpty(Replica);
        return Decode(writeVector).Contains(Replica, StringComparer.Ordinal);
    }

    /// <summary>
    /// Toggles one of the bits in <paramref name="writeVector"/>.
    /// </summary>
    /// <param name="writeVector">The write vector.</param>
    /// <param name="Replica">The replica whose bit is toggled.</param>
    /// <returns>The bit value after it is toggled.</returns>
    public static bool FlipBit(ref string writeVector, string Replica)
    {
        ArgumentNullException.ThrowIfNull(writeVector);
        ArgumentException.ThrowIfNullOrEmpty(Replica);

        var replicas = Decode(writeVector);
        var removed = false;
        for (var index = replicas.Count - 1; index >= 0; index--)
        {
            if (string.Equals(replicas[index], Replica, StringComparison.Ordinal))
            {
                replicas.RemoveAt(index);
                removed = true;
            }
        }

        if (!removed)
        {
            replicas.Add(Replica);
        }

        writeVector = Encode(replicas);
        return !removed;
    }

    private static List<string> Decode(string writeVector)
    {
        if (writeVector.Length == 0)
        {
            return [];
        }

        if (!writeVector.StartsWith(CurrentFormatPrefix, StringComparison.Ordinal))
        {
            return DecodeLegacy(writeVector);
        }

        var result = new List<string>();
        var position = CurrentFormatPrefix.Length;
        if (position == writeVector.Length)
        {
            throw new FormatException("The versioned write vector does not contain any replica identifiers.");
        }

        while (position < writeVector.Length)
        {
            var separator = writeVector.IndexOf(':', position);
            if (separator < 0
                || separator == position
                || !int.TryParse(
                    writeVector.AsSpan(position, separator - position),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var length)
                || length <= 0
                || length > writeVector.Length - separator - 1)
            {
                throw new FormatException("The write vector contains an invalid length-prefixed replica identifier.");
            }

            position = separator + 1;
            result.Add(writeVector.Substring(position, length));
            position += length;
        }

        return result;
    }

    private static List<string> DecodeLegacy(string writeVector)
    {
        if (writeVector[0] != ',')
        {
            throw new FormatException("The write vector has an unsupported format.");
        }

        var tokens = writeVector[1..].Split(',');
        if (tokens.Any(static token => token.Length == 0))
        {
            throw new FormatException("The legacy write vector contains an empty replica identifier.");
        }

        return [.. tokens];
    }

    private static string Encode(List<string> replicas)
    {
        if (replicas.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(CurrentFormatPrefix);
        foreach (var replica in replicas)
        {
            builder.Append(replica.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(replica);
        }

        return builder.ToString();
    }
}
