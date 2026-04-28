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
    /// Decodes a stored log extent.
    /// </summary>
    /// <remarks>
    /// Implementations take ownership of <paramref name="value"/> and must either return
    /// a <see cref="LogExtent"/> which owns it or dispose it before returning.
    /// </remarks>
    LogExtent Decode(ArcBuffer value);
}
