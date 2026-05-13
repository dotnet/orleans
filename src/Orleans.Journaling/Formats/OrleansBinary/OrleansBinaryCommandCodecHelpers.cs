using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Helpers shared by the Orleans binary durable command codecs.
/// </summary>
internal static class OrleansBinaryCommandCodecHelpers
{
    private const byte FormatVersion = 0;

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

    public static void WriteValue<T>(
        IFieldCodec<T> codec,
        T value,
        IBufferWriter<byte> output,
        SerializerSessionPool sessionPool)
    {
        using var session = sessionPool.GetSession();
        var writer = Writer.Create(output, session);
        codec.WriteField(ref writer, 0, typeof(T), value);
        writer.Commit();
    }

    public static T ReadValue<T, TInput>(IFieldCodec<T> codec, ref Reader<TInput> reader)
    {
        var field = reader.ReadFieldHeader();
        return codec.ReadValue(ref reader, field);
    }
}
