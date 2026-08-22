namespace Orleans.EventSourcing.Common;

public static class StringEncodedWriteVector
{
    // BitVector of replicas is implemented as a set of replica strings encoded within a string.
    // Each replica whose bit is set is stored as a comma-prefixed token.

    /// <summary>
    /// Gets one of the bits in <paramref name="writeVector"/>.
    /// </summary>
    /// <param name="writeVector">The write vector.</param>
    /// <param name="Replica">The replica whose bit is returned.</param>
    /// <returns><see langword="true"/> when the replica's bit is set.</returns>
    public static bool GetBit(string writeVector, string Replica)
    {
        var token = $",{Replica}";
        for (var pos = writeVector.IndexOf(token, StringComparison.Ordinal);
            pos >= 0;
            pos = writeVector.IndexOf(token, pos + 1, StringComparison.Ordinal))
        {
            var end = pos + token.Length;
            if (end == writeVector.Length || writeVector[end] == ',')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Toggles one of the bits in <paramref name="writeVector"/>.
    /// </summary>
    /// <param name="writeVector">The write vector.</param>
    /// <param name="Replica">The replica whose bit is toggled.</param>
    /// <returns>The bit value after it is toggled.</returns>
    public static bool FlipBit(ref string writeVector, string Replica)
    {
        var token = $",{Replica}";
        for (var pos = writeVector.IndexOf(token, StringComparison.Ordinal);
            pos >= 0;
            pos = writeVector.IndexOf(token, pos + 1, StringComparison.Ordinal))
        {
            var end = pos + token.Length;
            if (end == writeVector.Length || writeVector[end] == ',')
            {
                writeVector = writeVector.Remove(pos, token.Length);
                return false;
            }
        }

        writeVector = string.Concat(token, writeVector);
        return true;
    }
}
