namespace Orleans.Journaling;

/// <summary>
/// Base type for all DurableList log entries.
/// </summary>
public abstract record DurableListEntry<T>;

/// <summary>
/// Appends an item to the end of the list.
/// </summary>
public sealed record ListAddEntry<T>(T Item) : DurableListEntry<T>;

/// <summary>
/// Sets the item at a specific index.
/// </summary>
public sealed record ListSetEntry<T>(int Index, T Item) : DurableListEntry<T>;

/// <summary>
/// Inserts an item at a specific index.
/// </summary>
public sealed record ListInsertEntry<T>(int Index, T Item) : DurableListEntry<T>;

/// <summary>
/// Removes the item at a specific index.
/// </summary>
public sealed record ListRemoveAtEntry<T>(int Index) : DurableListEntry<T>;

/// <summary>
/// Clears all items from the list.
/// </summary>
public sealed record ListClearEntry<T>() : DurableListEntry<T>;

/// <summary>
/// A snapshot of the entire list state.
/// </summary>
public sealed record ListSnapshotEntry<T>(IReadOnlyList<T> Items) : DurableListEntry<T>;
