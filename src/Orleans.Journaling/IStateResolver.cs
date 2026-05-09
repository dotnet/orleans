namespace Orleans.Journaling;

/// <summary>
/// Resolves persisted journal stream ids to durable states during recovery.
/// </summary>
public interface IStateResolver
{
    /// <summary>
    /// Resolves the durable state for <paramref name="streamId"/>.
    /// </summary>
    /// <param name="streamId">The persisted journal stream id.</param>
    /// <returns>The durable state for the stream.</returns>
    IJournaledState ResolveState(JournalStreamId streamId);
}
