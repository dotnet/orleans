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
    public static void WriteHeader(IBufferWriter<byte> output, params ReadOnlySpan<uint> values)
    {
#pragma warning disable CA2000 // The active entry lends its destination; disposing this writer would dispose the owning JournalBufferWriter.
        var writer = Writer.Create(output, session: null!);
#pragma warning restore CA2000
        foreach (var value in values)
        {
            writer.WriteVarUInt32(value);
        }

        writer.Commit();
    }

    public static void WriteVersion(IBufferWriter<byte> output, ulong version)
    {
#pragma warning disable CA2000 // The active entry lends its destination; the JournalBufferWriter owns disposal.
        var writer = Writer.Create(output, session: null!);
#pragma warning restore CA2000
        writer.WriteVarUInt64(version);
        writer.Commit();
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
}
