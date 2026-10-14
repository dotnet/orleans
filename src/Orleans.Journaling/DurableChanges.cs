namespace Orleans.Journaling;

/// <summary>
/// Identifies how far an observed change has progressed toward durability.
/// </summary>
[GenerateSerializer]
public enum DurableChangeStatus : byte
{
    /// <summary>
    /// The change is volatile draft content. It exists only in the current activation's memory.
    /// </summary>
    Draft = 0,

    /// <summary>
    /// The change is applied in memory and becomes durable when the next journal write is acknowledged.
    /// </summary>
    Staged = 1,

    /// <summary>
    /// The change is durable in journal storage.
    /// </summary>
    Committed = 2,
}

/// <summary>
/// Selects which changes a watch stream delivers.
/// </summary>
/// <remarks>
/// Every stream delivers durable changes. <see cref="Staged"/> and <see cref="Drafts"/> add the changes which are not
/// yet durable, and can be combined. A stream without <see cref="Staged"/> never delivers a change before the journal
/// write which contains it is acknowledged.
/// </remarks>
[Flags]
[GenerateSerializer]
public enum DurableWatchMode : byte
{
    /// <summary>
    /// Delivers only durable changes, after the journal write which contains them is acknowledged.
    /// </summary>
    Committed = 0,

    /// <summary>
    /// Also delivers staged changes as they are made, followed by commit watermarks as journal writes are acknowledged.
    /// </summary>
    Staged = 1,

    /// <summary>
    /// Also delivers volatile draft content, such as the items of an open <see cref="IDurableLogDraft{T}"/>.
    /// State without drafts ignores this flag.
    /// </summary>
    Drafts = 2,
}

/// <summary>
/// Describes the latest observed value of an <see cref="IDurableValue{T}"/>.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>
/// Value watches deliver the latest value. A reader which falls behind receives the newest value and skips
/// intermediate values.
/// </remarks>
[GenerateSerializer]
public readonly record struct ValueChange<T>
{
    /// <summary>
    /// Gets the value.
    /// </summary>
    [Id(0)]
    public T? Value { get; init; }

    /// <summary>
    /// Gets the durability status of <see cref="Value"/>.
    /// </summary>
    [Id(1)]
    public DurableChangeStatus Status { get; init; }

    /// <summary>
    /// Gets the activation-local write version which contains <see cref="Value"/>.
    /// </summary>
    [Id(2)]
    public long Version { get; init; }
}

/// <summary>
/// Identifies the kind of a <see cref="DictionaryChange{TKey, TValue}"/>.
/// </summary>
[GenerateSerializer]
public enum DictionaryChangeKind : byte
{
    /// <summary>
    /// <see cref="DictionaryChange{TKey, TValue}.Snapshot"/> contains the complete contents. Replace any local copy with it.
    /// </summary>
    Resync = 0,

    /// <summary>
    /// A key was added or its value was replaced.
    /// </summary>
    Set = 1,

    /// <summary>
    /// A key was removed.
    /// </summary>
    Removed = 2,

    /// <summary>
    /// All entries were removed.
    /// </summary>
    Cleared = 3,

    /// <summary>
    /// Staged changes with versions up to and including <see cref="DictionaryChange{TKey, TValue}.Version"/> are committed.
    /// </summary>
    Watermark = 4,
}

/// <summary>
/// Describes a change to an <see cref="IDurableDictionary{TKey, TValue}"/>.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
[GenerateSerializer]
public readonly record struct DictionaryChange<TKey, TValue> : IDurableChange<DictionaryChange<TKey, TValue>>
    where TKey : notnull
{
    /// <summary>
    /// Gets the kind of change.
    /// </summary>
    [Id(0)]
    public DictionaryChangeKind Kind { get; init; }

    /// <summary>
    /// Gets the affected key for <see cref="DictionaryChangeKind.Set"/> and <see cref="DictionaryChangeKind.Removed"/>.
    /// </summary>
    [Id(1)]
    public TKey? Key { get; init; }

    /// <summary>
    /// Gets the new value for <see cref="DictionaryChangeKind.Set"/>.
    /// </summary>
    [Id(2)]
    public TValue? Value { get; init; }

    /// <summary>
    /// Gets the complete contents for <see cref="DictionaryChangeKind.Resync"/>.
    /// </summary>
    [Id(3)]
    public IReadOnlyList<KeyValuePair<TKey, TValue>>? Snapshot { get; init; }

    /// <summary>
    /// Gets the durability status of the change.
    /// </summary>
    [Id(4)]
    public DurableChangeStatus Status { get; init; }

    /// <summary>
    /// Gets the activation-local write version which contains the change, or the committed version for <see cref="DictionaryChangeKind.Watermark"/>.
    /// </summary>
    [Id(5)]
    public long Version { get; init; }

    static DictionaryChange<TKey, TValue> IDurableChange<DictionaryChange<TKey, TValue>>.CreateWatermark(long version) =>
        new() { Kind = DictionaryChangeKind.Watermark, Status = DurableChangeStatus.Committed, Version = version };

    DictionaryChange<TKey, TValue> IDurableChange<DictionaryChange<TKey, TValue>>.WithStatus(DurableChangeStatus status, long version) =>
        this with { Status = status, Version = version };
}

/// <summary>
/// Identifies the kind of a <see cref="ListChange{T}"/>.
/// </summary>
[GenerateSerializer]
public enum ListChangeKind : byte
{
    /// <summary>
    /// <see cref="ListChange{T}.Snapshot"/> contains the complete contents. Replace any local copy with it.
    /// </summary>
    Resync = 0,

    /// <summary>
    /// An item was added to the end of the list.
    /// </summary>
    Added = 1,

    /// <summary>
    /// The item at <see cref="ListChange{T}.Index"/> was replaced.
    /// </summary>
    Set = 2,

    /// <summary>
    /// An item was inserted at <see cref="ListChange{T}.Index"/>.
    /// </summary>
    Inserted = 3,

    /// <summary>
    /// The item at <see cref="ListChange{T}.Index"/> was removed.
    /// </summary>
    RemovedAt = 4,

    /// <summary>
    /// All items were removed.
    /// </summary>
    Cleared = 5,

    /// <summary>
    /// Staged changes with versions up to and including <see cref="ListChange{T}.Version"/> are committed.
    /// </summary>
    Watermark = 6,
}

/// <summary>
/// Describes a change to an <see cref="IDurableList{T}"/>.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
[GenerateSerializer]
public readonly record struct ListChange<T> : IDurableChange<ListChange<T>>
{
    /// <summary>
    /// Gets the kind of change.
    /// </summary>
    [Id(0)]
    public ListChangeKind Kind { get; init; }

    /// <summary>
    /// Gets the affected index for <see cref="ListChangeKind.Added"/>, <see cref="ListChangeKind.Set"/>,
    /// <see cref="ListChangeKind.Inserted"/>, and <see cref="ListChangeKind.RemovedAt"/>.
    /// </summary>
    [Id(1)]
    public int Index { get; init; }

    /// <summary>
    /// Gets the added, assigned, or inserted item.
    /// </summary>
    [Id(2)]
    public T? Item { get; init; }

    /// <summary>
    /// Gets the complete contents for <see cref="ListChangeKind.Resync"/>.
    /// </summary>
    [Id(3)]
    public IReadOnlyList<T>? Snapshot { get; init; }

    /// <summary>
    /// Gets the durability status of the change.
    /// </summary>
    [Id(4)]
    public DurableChangeStatus Status { get; init; }

    /// <summary>
    /// Gets the activation-local write version which contains the change, or the committed version for <see cref="ListChangeKind.Watermark"/>.
    /// </summary>
    [Id(5)]
    public long Version { get; init; }

    static ListChange<T> IDurableChange<ListChange<T>>.CreateWatermark(long version) =>
        new() { Kind = ListChangeKind.Watermark, Status = DurableChangeStatus.Committed, Version = version };

    ListChange<T> IDurableChange<ListChange<T>>.WithStatus(DurableChangeStatus status, long version) =>
        this with { Status = status, Version = version };
}

/// <summary>
/// Identifies the kind of a <see cref="SetChange{T}"/>.
/// </summary>
[GenerateSerializer]
public enum SetChangeKind : byte
{
    /// <summary>
    /// <see cref="SetChange{T}.Snapshot"/> contains the complete contents. Replace any local copy with it.
    /// </summary>
    Resync = 0,

    /// <summary>
    /// An item was added.
    /// </summary>
    Added = 1,

    /// <summary>
    /// An item was removed.
    /// </summary>
    Removed = 2,

    /// <summary>
    /// All items were removed.
    /// </summary>
    Cleared = 3,

    /// <summary>
    /// Staged changes with versions up to and including <see cref="SetChange{T}.Version"/> are committed.
    /// </summary>
    Watermark = 4,
}

/// <summary>
/// Describes a change to an <see cref="IDurableSet{T}"/>.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
[GenerateSerializer]
public readonly record struct SetChange<T> : IDurableChange<SetChange<T>>
{
    /// <summary>
    /// Gets the kind of change.
    /// </summary>
    [Id(0)]
    public SetChangeKind Kind { get; init; }

    /// <summary>
    /// Gets the added or removed item.
    /// </summary>
    [Id(1)]
    public T? Item { get; init; }

    /// <summary>
    /// Gets the complete contents for <see cref="SetChangeKind.Resync"/>.
    /// </summary>
    [Id(2)]
    public IReadOnlyList<T>? Snapshot { get; init; }

    /// <summary>
    /// Gets the durability status of the change.
    /// </summary>
    [Id(3)]
    public DurableChangeStatus Status { get; init; }

    /// <summary>
    /// Gets the activation-local write version which contains the change, or the committed version for <see cref="SetChangeKind.Watermark"/>.
    /// </summary>
    [Id(4)]
    public long Version { get; init; }

    static SetChange<T> IDurableChange<SetChange<T>>.CreateWatermark(long version) =>
        new() { Kind = SetChangeKind.Watermark, Status = DurableChangeStatus.Committed, Version = version };

    SetChange<T> IDurableChange<SetChange<T>>.WithStatus(DurableChangeStatus status, long version) =>
        this with { Status = status, Version = version };
}

/// <summary>
/// Identifies the kind of a <see cref="QueueChange{T}"/>.
/// </summary>
[GenerateSerializer]
public enum QueueChangeKind : byte
{
    /// <summary>
    /// <see cref="QueueChange{T}.Snapshot"/> contains the complete contents in queue order. Replace any local copy with it.
    /// </summary>
    Resync = 0,

    /// <summary>
    /// An item was added to the end of the queue.
    /// </summary>
    Enqueued = 1,

    /// <summary>
    /// The item at the beginning of the queue was removed.
    /// </summary>
    Dequeued = 2,

    /// <summary>
    /// All items were removed.
    /// </summary>
    Cleared = 3,

    /// <summary>
    /// Staged changes with versions up to and including <see cref="QueueChange{T}.Version"/> are committed.
    /// </summary>
    Watermark = 4,
}

/// <summary>
/// Describes a change to an <see cref="IDurableQueue{T}"/>.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
[GenerateSerializer]
public readonly record struct QueueChange<T> : IDurableChange<QueueChange<T>>
{
    /// <summary>
    /// Gets the kind of change.
    /// </summary>
    [Id(0)]
    public QueueChangeKind Kind { get; init; }

    /// <summary>
    /// Gets the enqueued or dequeued item.
    /// </summary>
    [Id(1)]
    public T? Item { get; init; }

    /// <summary>
    /// Gets the complete contents for <see cref="QueueChangeKind.Resync"/>, in queue order.
    /// </summary>
    [Id(2)]
    public IReadOnlyList<T>? Snapshot { get; init; }

    /// <summary>
    /// Gets the durability status of the change.
    /// </summary>
    [Id(3)]
    public DurableChangeStatus Status { get; init; }

    /// <summary>
    /// Gets the activation-local write version which contains the change, or the committed version for <see cref="QueueChangeKind.Watermark"/>.
    /// </summary>
    [Id(4)]
    public long Version { get; init; }

    static QueueChange<T> IDurableChange<QueueChange<T>>.CreateWatermark(long version) =>
        new() { Kind = QueueChangeKind.Watermark, Status = DurableChangeStatus.Committed, Version = version };

    QueueChange<T> IDurableChange<QueueChange<T>>.WithStatus(DurableChangeStatus status, long version) =>
        this with { Status = status, Version = version };
}

/// <summary>
/// Supplies the status stamping and watermark construction used by change watchers.
/// </summary>
internal interface IDurableChange<TSelf> where TSelf : struct, IDurableChange<TSelf>
{
    static abstract TSelf CreateWatermark(long version);

    TSelf WithStatus(DurableChangeStatus status, long version);
}
