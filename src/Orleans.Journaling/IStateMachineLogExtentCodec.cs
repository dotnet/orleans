using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Encodes and decodes physical state machine log extents.
/// </summary>
/// <remarks>
/// Durable state machine codecs encode the payload for one state machine operation.
/// This codec encodes the storage extent which contains one or more operation payloads
/// together with their state machine identifiers.
/// </remarks>
public interface IStateMachineLogExtentCodec
{
    /// <summary>
    /// Encodes a log extent for storage.
    /// </summary>
    byte[] Encode(LogExtentBuilder value);

    /// <summary>
    /// Encodes a log extent as a readable stream.
    /// </summary>
    /// <remarks>
    /// Implementations should avoid intermediate byte arrays when their physical format can be streamed directly.
    /// The caller owns the returned stream and must dispose it when the storage operation completes.
    /// </remarks>
    Stream EncodeToStream(LogExtentBuilder value) => new MemoryStream(Encode(value), writable: false);

    /// <summary>
    /// Decodes a stored log extent.
    /// </summary>
    /// <remarks>
    /// Implementations take ownership of <paramref name="value"/> and must either return
    /// a <see cref="LogExtent"/> which owns it or dispose it before returning.
    /// </remarks>
    LogExtent Decode(ArcBuffer value);

    /// <summary>
    /// Reads a stored log extent and pushes its entries to <paramref name="consumer"/>.
    /// </summary>
    /// <remarks>
    /// Implementations take ownership of <paramref name="value"/> and must dispose it before returning.
    /// </remarks>
    void Read(ArcBuffer value, IStateMachineLogEntryConsumer consumer)
    {
        using var extent = Decode(value);
        foreach (var entry in extent.Entries)
        {
            consumer.OnEntry(entry.StreamId, entry.Payload);
        }
    }
}
