using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;

namespace Orleans.Journaling;

/// <summary>
/// Helpers shared by the public ROS-based <c>IDurable*OperationCodec.Apply</c> entry points.
/// </summary>
internal static class OrleansBinaryOperationApplier
{
    private const byte FormatVersion = 0;

    /// <summary>
    /// Copies <paramref name="input"/> into a freshly-allocated <see cref="ArcBuffer"/> and returns the pinned slice.
    /// The caller is responsible for disposing the returned buffer.
    /// </summary>
    public static ArcBuffer Materialize(ReadOnlySequence<byte> input)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(input);
        return writer.PeekSlice(writer.Length);
    }

    /// <summary>
    /// Reads and validates the format-version byte at the current reader position.
    /// </summary>
    public static void ReadVersion(ref Reader<ArcBufferReaderInput> reader)
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
