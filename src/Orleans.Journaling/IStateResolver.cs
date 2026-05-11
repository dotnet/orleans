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

/// <summary>
/// Resolves the operation codec used to apply journal entries during recovery.
/// </summary>
public interface IJournalOperationCodecResolver : IStateResolver
{
    /// <summary>
    /// Gets the operation codec to use when applying an entry to <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The state receiving the operation.</param>
    /// <returns>The operation codec for the current recovery format.</returns>
    object GetOperationCodec(IJournaledState state);
}
