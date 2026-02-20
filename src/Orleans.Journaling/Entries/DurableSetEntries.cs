namespace Orleans.Journaling;

/// <summary>
/// Base type for all DurableSet log entries.
/// </summary>
public abstract record DurableSetEntry<T>;

/// <summary>
/// Adds an item to the set.
/// </summary>
public sealed record SetAddEntry<T>(T Item) : DurableSetEntry<T>;

/// <summary>
/// Removes an item from the set.
/// </summary>
public sealed record SetRemoveEntry<T>(T Item) : DurableSetEntry<T>;

/// <summary>
/// Clears all items from the set.
/// </summary>
public sealed record SetClearEntry<T>() : DurableSetEntry<T>;

/// <summary>
/// A snapshot of the entire set state.
/// </summary>
public sealed record SetSnapshotEntry<T>(IReadOnlyList<T> Items) : DurableSetEntry<T>;
