using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Helpers shared by the public ROS-based <c>*OperationCodec.Apply</c> entry points.
/// </summary>
internal static class OrleansBinaryOperationApplier
{
    private const byte FormatVersion = 0;

    /// <summary>
    /// Creates a reader directly over <paramref name="input"/>.
    /// </summary>
    public static Reader<ReadOnlySequenceInput> CreateReader(ReadOnlySequence<byte> input, SerializerSession session) =>
        Reader.Create(input, session);

    /// <summary>
    /// Reads and validates the format-version byte at the current reader position.
    /// </summary>
    public static void ReadVersion<TInput>(ref Reader<TInput> reader)
    {
        if (reader.Position >= reader.Length)
        {
            throw new InvalidOperationException("Missing binary journal entry format version byte.");
        }

        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new NotSupportedException($"Unsupported format version {version} for binary journal entry.");
        }
    }
}
