namespace Orleans.Journaling;

/// <summary>
/// Resolves persisted journal stream ids to durable state machines during recovery.
/// </summary>
public interface IStateMachineResolver
{
    /// <summary>
    /// Resolves the durable state machine for <paramref name="streamId"/>.
    /// </summary>
    /// <param name="streamId">The persisted journal stream id.</param>
    /// <returns>The durable state machine for the stream.</returns>
    IDurableStateMachine ResolveStateMachine(JournalStreamId streamId);
}
