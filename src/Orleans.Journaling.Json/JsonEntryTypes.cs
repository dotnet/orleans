using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Journaling.Json;

/// <summary>
/// Non-generic JSON representation of a dictionary log entry.
/// Values are stored as <see cref="JsonElement"/> for deferred deserialization.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "cmd")]
[JsonDerivedType(typeof(JsonDictionarySetEntry), "set")]
[JsonDerivedType(typeof(JsonDictionaryRemoveEntry), "remove")]
[JsonDerivedType(typeof(JsonDictionaryClearEntry), "clear")]
[JsonDerivedType(typeof(JsonDictionarySnapshotEntry), "snapshot")]
internal abstract record JsonDictionaryEntry;

internal sealed record JsonDictionarySetEntry(JsonElement Key, JsonElement Value) : JsonDictionaryEntry;
internal sealed record JsonDictionaryRemoveEntry(JsonElement Key) : JsonDictionaryEntry;
internal sealed record JsonDictionaryClearEntry() : JsonDictionaryEntry;
internal sealed record JsonDictionarySnapshotEntry(IReadOnlyList<JsonDictionarySnapshotItem> Items) : JsonDictionaryEntry;
internal sealed record JsonDictionarySnapshotItem(JsonElement Key, JsonElement Value);

/// <summary>
/// Non-generic JSON representation of a list log entry.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "cmd")]
[JsonDerivedType(typeof(JsonListAddEntry), "add")]
[JsonDerivedType(typeof(JsonListSetEntry), "set")]
[JsonDerivedType(typeof(JsonListInsertEntry), "insert")]
[JsonDerivedType(typeof(JsonListRemoveAtEntry), "removeAt")]
[JsonDerivedType(typeof(JsonListClearEntry), "clear")]
[JsonDerivedType(typeof(JsonListSnapshotEntry), "snapshot")]
internal abstract record JsonListEntry;

internal sealed record JsonListAddEntry(JsonElement Item) : JsonListEntry;
internal sealed record JsonListSetEntry(int Index, JsonElement Item) : JsonListEntry;
internal sealed record JsonListInsertEntry(int Index, JsonElement Item) : JsonListEntry;
internal sealed record JsonListRemoveAtEntry(int Index) : JsonListEntry;
internal sealed record JsonListClearEntry() : JsonListEntry;
internal sealed record JsonListSnapshotEntry(IReadOnlyList<JsonElement> Items) : JsonListEntry;

/// <summary>
/// Non-generic JSON representation of a queue log entry.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "cmd")]
[JsonDerivedType(typeof(JsonQueueEnqueueEntry), "enqueue")]
[JsonDerivedType(typeof(JsonQueueDequeueEntry), "dequeue")]
[JsonDerivedType(typeof(JsonQueueClearEntry), "clear")]
[JsonDerivedType(typeof(JsonQueueSnapshotEntry), "snapshot")]
internal abstract record JsonQueueEntry;

internal sealed record JsonQueueEnqueueEntry(JsonElement Item) : JsonQueueEntry;
internal sealed record JsonQueueDequeueEntry() : JsonQueueEntry;
internal sealed record JsonQueueClearEntry() : JsonQueueEntry;
internal sealed record JsonQueueSnapshotEntry(IReadOnlyList<JsonElement> Items) : JsonQueueEntry;

/// <summary>
/// Non-generic JSON representation of a set log entry.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "cmd")]
[JsonDerivedType(typeof(JsonSetAddEntry), "add")]
[JsonDerivedType(typeof(JsonSetRemoveEntry), "remove")]
[JsonDerivedType(typeof(JsonSetClearEntry), "clear")]
[JsonDerivedType(typeof(JsonSetSnapshotEntry), "snapshot")]
internal abstract record JsonSetEntry;

internal sealed record JsonSetAddEntry(JsonElement Item) : JsonSetEntry;
internal sealed record JsonSetRemoveEntry(JsonElement Item) : JsonSetEntry;
internal sealed record JsonSetClearEntry() : JsonSetEntry;
internal sealed record JsonSetSnapshotEntry(IReadOnlyList<JsonElement> Items) : JsonSetEntry;

/// <summary>
/// Non-generic JSON representation of a value log entry.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "cmd")]
[JsonDerivedType(typeof(JsonValueSetEntry), "set")]
internal abstract record JsonValueEntry;

internal sealed record JsonValueSetEntry(JsonElement Value) : JsonValueEntry;

/// <summary>
/// Non-generic JSON representation of a state log entry.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "cmd")]
[JsonDerivedType(typeof(JsonStateSetEntry), "set")]
[JsonDerivedType(typeof(JsonStateClearEntry), "clear")]
internal abstract record JsonStateEntry;

internal sealed record JsonStateSetEntry(JsonElement State, ulong Version) : JsonStateEntry;
internal sealed record JsonStateClearEntry() : JsonStateEntry;

/// <summary>
/// Non-generic JSON representation of a task completion source log entry.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "cmd")]
[JsonDerivedType(typeof(JsonTcsCompletedEntry), "completed")]
[JsonDerivedType(typeof(JsonTcsFaultedEntry), "faulted")]
[JsonDerivedType(typeof(JsonTcsCanceledEntry), "canceled")]
[JsonDerivedType(typeof(JsonTcsPendingEntry), "pending")]
internal abstract record JsonTcsEntry;

internal sealed record JsonTcsCompletedEntry(JsonElement Value) : JsonTcsEntry;
internal sealed record JsonTcsFaultedEntry(string Exception) : JsonTcsEntry;
internal sealed record JsonTcsCanceledEntry() : JsonTcsEntry;
internal sealed record JsonTcsPendingEntry() : JsonTcsEntry;
