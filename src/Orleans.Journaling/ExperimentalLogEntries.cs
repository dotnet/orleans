using System.Buffers;

namespace Orleans.Journaling;

internal readonly record struct LogEntryCommand(uint? Tag, string? Name)
{
    public bool Is<TEntry>() where TEntry : ILogEntry<TEntry>
        => (Tag is { } tag && tag == TEntry.Tag)
            || (Name is { } name && string.Equals(name, TEntry.Name, StringComparison.Ordinal));
}

internal interface ILogEntry<TEntry> where TEntry : ILogEntry<TEntry>
{
    static abstract uint Tag { get; }
    static abstract string Name { get; }
    static abstract void Write<TWriter>(ref TWriter writer, in TEntry entry)
        where TWriter : ILogEntryWriter;
}

internal interface ILogEntry<TEntry, TConsumer> : ILogEntry<TEntry>
    where TEntry : ILogEntry<TEntry, TConsumer>
{
    static abstract void Apply<TReader>(ref TReader reader, TConsumer consumer)
        where TReader : ILogEntryReader;
}

internal interface ILogEntryWriter
{
    void WriteCommand(uint tag, string name);
    void WriteField<T>(uint tag, string name, T value);
    void WriteRepeated<T>(uint tag, string name, uint countTag, string countName, IEnumerable<T> values, int count);
    void WriteKeyValuePairs<TKey, TValue>(
        uint tag,
        string name,
        uint countTag,
        string countName,
        uint keyTag,
        string keyName,
        uint valueTag,
        string valueName,
        IEnumerable<KeyValuePair<TKey, TValue>> values,
        int count);
    void Complete();
}

internal interface ILogEntryReader
{
    T ReadField<T>(uint tag, string name);
    void ReadRepeated<T>(uint tag, string name, uint countTag, string countName, Action<int> start, Action<T> item);
    void ReadKeyValuePairs<TKey, TValue>(
        uint tag,
        string name,
        uint countTag,
        string countName,
        uint keyTag,
        string keyName,
        uint valueTag,
        string valueName,
        Action<int> start,
        Action<TKey, TValue> item);
}

internal interface ILogEntryCodec
{
    void WriteEntry<TEntry>(TEntry entry, IBufferWriter<byte> output)
        where TEntry : ILogEntry<TEntry>;
    LogEntryCommand ReadCommand(ReadOnlySequence<byte> input);
    void ApplyEntry<TEntry, TConsumer>(ReadOnlySequence<byte> input, TConsumer consumer)
        where TEntry : ILogEntry<TEntry, TConsumer>;
}

internal static class QueueLogEntries
{
    private const uint ItemField = 2;
    private const uint CountField = 3;
    private const string ItemName = "item";
    private const string ItemsName = "items";
    private const string CountName = "count";

    internal readonly record struct Enqueue<T>(T Item) : ILogEntry<Enqueue<T>, IDurableQueueLogEntryConsumer<T>>
    {
        public const uint CommandTag = 0;
        public const string CommandName = "enqueue";
        public static uint Tag => CommandTag;
        public static string Name => CommandName;
        public static void Write<TWriter>(ref TWriter writer, in Enqueue<T> entry)
            where TWriter : ILogEntryWriter
            => writer.WriteField(ItemField, ItemName, entry.Item);
        public static void Apply<TReader>(ref TReader reader, IDurableQueueLogEntryConsumer<T> consumer)
            where TReader : ILogEntryReader
            => consumer.ApplyEnqueue(reader.ReadField<T>(ItemField, ItemName));
    }

    internal readonly record struct Dequeue<T> : ILogEntry<Dequeue<T>, IDurableQueueLogEntryConsumer<T>>
    {
        public const uint CommandTag = 1;
        public const string CommandName = "dequeue";
        public static uint Tag => CommandTag;
        public static string Name => CommandName;
        public static void Write<TWriter>(ref TWriter writer, in Dequeue<T> entry)
            where TWriter : ILogEntryWriter
        {
        }

        public static void Apply<TReader>(ref TReader reader, IDurableQueueLogEntryConsumer<T> consumer)
            where TReader : ILogEntryReader
            => consumer.ApplyDequeue();
    }

    internal readonly record struct Clear<T> : ILogEntry<Clear<T>, IDurableQueueLogEntryConsumer<T>>
    {
        public const uint CommandTag = 2;
        public const string CommandName = "clear";
        public static uint Tag => CommandTag;
        public static string Name => CommandName;
        public static void Write<TWriter>(ref TWriter writer, in Clear<T> entry)
            where TWriter : ILogEntryWriter
        {
        }

        public static void Apply<TReader>(ref TReader reader, IDurableQueueLogEntryConsumer<T> consumer)
            where TReader : ILogEntryReader
            => consumer.ApplyClear();
    }

    internal readonly record struct Snapshot<T>(IEnumerable<T> Items, int Count) : ILogEntry<Snapshot<T>, IDurableQueueLogEntryConsumer<T>>
    {
        public const uint CommandTag = 3;
        public const string CommandName = "snapshot";
        public static uint Tag => CommandTag;
        public static string Name => CommandName;
        public static void Write<TWriter>(ref TWriter writer, in Snapshot<T> entry)
            where TWriter : ILogEntryWriter
            => writer.WriteRepeated(ItemField, ItemsName, CountField, CountName, entry.Items, entry.Count);
        public static void Apply<TReader>(ref TReader reader, IDurableQueueLogEntryConsumer<T> consumer)
            where TReader : ILogEntryReader
            => reader.ReadRepeated<T>(ItemField, ItemsName, CountField, CountName, consumer.ApplySnapshotStart, consumer.ApplySnapshotItem);
    }
}

internal static class DictionaryLogEntries
{
    private const uint KeyField = 2;
    private const uint ValueField = 3;
    private const uint CountField = 4;
    private const uint ItemsField = 5;
    private const string KeyName = "key";
    private const string ValueName = "value";
    private const string CountName = "count";
    private const string ItemsName = "items";

    internal readonly record struct Set<TKey, TValue>(TKey Key, TValue Value)
        : ILogEntry<Set<TKey, TValue>, IDurableDictionaryLogEntryConsumer<TKey, TValue>> where TKey : notnull
    {
        public const uint CommandTag = 0;
        public const string CommandName = "set";
        public static uint Tag => CommandTag;
        public static string Name => CommandName;
        public static void Write<TWriter>(ref TWriter writer, in Set<TKey, TValue> entry)
            where TWriter : ILogEntryWriter
        {
            writer.WriteField(KeyField, KeyName, entry.Key);
            writer.WriteField(ValueField, ValueName, entry.Value);
        }

        public static void Apply<TReader>(ref TReader reader, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer)
            where TReader : ILogEntryReader
            => consumer.ApplySet(reader.ReadField<TKey>(KeyField, KeyName), reader.ReadField<TValue>(ValueField, ValueName));
    }

    internal readonly record struct Remove<TKey, TValue>(TKey Key)
        : ILogEntry<Remove<TKey, TValue>, IDurableDictionaryLogEntryConsumer<TKey, TValue>> where TKey : notnull
    {
        public const uint CommandTag = 1;
        public const string CommandName = "remove";
        public static uint Tag => CommandTag;
        public static string Name => CommandName;
        public static void Write<TWriter>(ref TWriter writer, in Remove<TKey, TValue> entry)
            where TWriter : ILogEntryWriter
            => writer.WriteField(KeyField, KeyName, entry.Key);
        public static void Apply<TReader>(ref TReader reader, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer)
            where TReader : ILogEntryReader
            => consumer.ApplyRemove(reader.ReadField<TKey>(KeyField, KeyName));
    }

    internal readonly record struct Clear<TKey, TValue>
        : ILogEntry<Clear<TKey, TValue>, IDurableDictionaryLogEntryConsumer<TKey, TValue>> where TKey : notnull
    {
        public const uint CommandTag = 2;
        public const string CommandName = "clear";
        public static uint Tag => CommandTag;
        public static string Name => CommandName;
        public static void Write<TWriter>(ref TWriter writer, in Clear<TKey, TValue> entry)
            where TWriter : ILogEntryWriter
        {
        }

        public static void Apply<TReader>(ref TReader reader, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer)
            where TReader : ILogEntryReader
            => consumer.ApplyClear();
    }

    internal readonly record struct Snapshot<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> Items, int Count)
        : ILogEntry<Snapshot<TKey, TValue>, IDurableDictionaryLogEntryConsumer<TKey, TValue>> where TKey : notnull
    {
        public const uint CommandTag = 3;
        public const string CommandName = "snapshot";
        public static uint Tag => CommandTag;
        public static string Name => CommandName;
        public static void Write<TWriter>(ref TWriter writer, in Snapshot<TKey, TValue> entry)
            where TWriter : ILogEntryWriter
            => writer.WriteKeyValuePairs(ItemsField, ItemsName, CountField, CountName, KeyField, KeyName, ValueField, ValueName, entry.Items, entry.Count);
        public static void Apply<TReader>(ref TReader reader, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer)
            where TReader : ILogEntryReader
            => reader.ReadKeyValuePairs<TKey, TValue>(
                ItemsField,
                ItemsName,
                CountField,
                CountName,
                KeyField,
                KeyName,
                ValueField,
                ValueName,
                consumer.ApplySnapshotStart,
                consumer.ApplySnapshotItem);
    }
}
