using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// Reads and writes Orleans binary command values using an isolated serializer session for each value.
/// </summary>
internal static class OrleansBinaryCommandCodecHelpers
{
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
        reader.Session.Reset();
        try
        {
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field)!;
        }
        finally
        {
            reader.Session.Reset();
        }
    }

    public static T ReadIndependentValue<T, TInput>(IFieldCodec<T> codec, ref Reader<TInput> reader)
    {
        var result = ReadValue(codec, ref reader);
        // Snapshot writers serialize each value with a fresh session, so replay must use the same boundary.
        reader.Session.Reset();
        return result;
    }
}
