namespace Orleans.Durable.Journaling;

/// <summary>
/// Marks a state which reports every change it stages to the calling flow's <see cref="StagedChangeScope"/>: the state
/// commits a journal entry when the change is made, or calls <see cref="JournalStreamWriter.MarkModified"/>.
/// </summary>
/// <remarks>
/// A state which journals changes it can't observe, such as an object mutated in place and serialized whole when a write
/// captures it, doesn't implement this interface. While one is registered, the manager can't tell whether a flow staged
/// a change, so every <see cref="StagedChangeScope"/> reports staged changes.
/// </remarks>
internal interface IReportsStagedChanges;
