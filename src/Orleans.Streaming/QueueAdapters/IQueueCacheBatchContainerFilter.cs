namespace Orleans.Streams;

/// <summary>
/// Slices a provider record for event-level subscription resume positions.
/// </summary>
/// <remarks>
/// Slices preserve stream identity, provider position metadata, event indexes, and request context.
/// Slicing must leave the original batch available for retry. An unchanged batch may be
/// returned directly; a nonempty slice exposes the first remaining event's position through
/// <see cref="IBatchContainer.SequenceToken"/>.
/// </remarks>
public interface IQueueCacheBatchContainerFilter
{
    /// <summary>
    /// Returns the events at or after the inclusive start position.
    /// </summary>
    /// <param name="inclusiveStartToken">The inclusive event position.</param>
    /// <returns>The remaining batch, or <see langword="null"/> when the whole record precedes the requested position.</returns>
    IBatchContainer? FilterFrom(StreamSequenceToken inclusiveStartToken);

    /// <summary>
    /// Returns the events after the acknowledged position.
    /// </summary>
    /// <param name="exclusiveStartToken">The acknowledged event position.</param>
    /// <returns>The remaining batch, or <see langword="null"/> when the record is fully accounted for.</returns>
    IBatchContainer? FilterAfter(StreamSequenceToken exclusiveStartToken);
}
