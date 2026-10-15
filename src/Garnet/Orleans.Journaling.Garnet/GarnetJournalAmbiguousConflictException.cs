using Orleans.Storage;

namespace Orleans.Journaling;

/// <summary>
/// A journal write found neither its own ETag among the journal's recent mutations nor its condition satisfied, on an
/// attempt which followed an attempt whose outcome is unknown.
/// </summary>
/// <remarks>
/// The earlier attempt may have committed before more than <see cref="GarnetJournalProtocol.RecentETagCount"/> later
/// mutations by other writers, so whether this write took effect is unknown. Other writers changed the journal either
/// way, so the caller treats it as a conflict and reads the journal again.
/// </remarks>
[GenerateSerializer]
internal sealed class GarnetJournalAmbiguousConflictException : InconsistentStateException
{
    public GarnetJournalAmbiguousConflictException(string message)
        : base(message)
    {
    }
}
