namespace Orleans.Journaling;

/// <summary>
/// Marks a state whose every change advances <see cref="JournaledStateManager.StagedChangeCount"/>: the state commits a
/// journal entry when the change is made, or calls <see cref="JournalStreamWriter.MarkModified"/>.
/// </summary>
/// <remarks>
/// A state which journals changes it can't observe, such as an object mutated in place and serialized whole when a write
/// captures it, doesn't implement this interface. While one is registered, the manager can't tell whether a state staged
/// a change, so <see cref="JournaledStateManager.StagedChangeCount"/> is negative.
/// </remarks>
internal interface IReportsStagedChanges;
