using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class DurableCollectionDirectWriteTests
{
    [Fact]
    public void Queue_Enqueue_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var queue = new DurableQueue<int>("queue", new TestJournalManager(writer), new ThrowingQueueCodec<int>(throwOnEnqueue: true));

        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(1));

        Assert.Empty(queue);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Queue_Dequeue_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var queue = new DurableQueue<int>("queue", new TestJournalManager(writer), new ThrowingQueueCodec<int>(throwOnDequeue: true));
        ((IDurableQueueOperationHandler<int>)queue).ApplyEnqueue(1);

        Assert.Throws<InvalidOperationException>(() => queue.Dequeue());

        Assert.Single(queue);
        Assert.Equal(1, queue.Peek());
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Queue_Clear_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var queue = new DurableQueue<int>("queue", new TestJournalManager(writer), new ThrowingQueueCodec<int>(throwOnClear: true));
        ((IDurableQueueOperationHandler<int>)queue).ApplyEnqueue(1);
        ((IDurableQueueOperationHandler<int>)queue).ApplyEnqueue(2);

        Assert.Throws<InvalidOperationException>(queue.Clear);

        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.Peek());
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Set_Add_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var set = new DurableSet<int>("set", new TestJournalManager(writer), new ThrowingSetCodec<int>(throwOnAdd: true));

        Assert.Throws<InvalidOperationException>(() => set.Add(1));

        Assert.Empty(set);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Set_Remove_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var set = new DurableSet<int>("set", new TestJournalManager(writer), new ThrowingSetCodec<int>(throwOnRemove: true));
        ((IDurableSetOperationHandler<int>)set).ApplyAdd(1);

        Assert.Throws<InvalidOperationException>(() => set.Remove(1));

        Assert.Contains(1, (IEnumerable<int>)set);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Set_Clear_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var set = new DurableSet<int>("set", new TestJournalManager(writer), new ThrowingSetCodec<int>(throwOnClear: true));
        ((IDurableSetOperationHandler<int>)set).ApplyAdd(1);
        ((IDurableSetOperationHandler<int>)set).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(set.Clear);

        Assert.True(set.SetEquals([1, 2]));
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Set_IntersectWith_DoesNotMutateWhenSnapshotEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var set = new DurableSet<int>("set", new TestJournalManager(writer), new ThrowingSetCodec<int>(throwOnSnapshot: true));
        ((IDurableSetOperationHandler<int>)set).ApplyAdd(1);
        ((IDurableSetOperationHandler<int>)set).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(() => set.IntersectWith([2, 3]));

        Assert.Contains(1, (IEnumerable<int>)set);
        Assert.Contains(2, (IEnumerable<int>)set);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Set_SymmetricExceptWith_DoesNotMutateWhenSnapshotEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var set = new DurableSet<int>("set", new TestJournalManager(writer), new ThrowingSetCodec<int>(throwOnSnapshot: true));
        ((IDurableSetOperationHandler<int>)set).ApplyAdd(1);
        ((IDurableSetOperationHandler<int>)set).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(() => set.SymmetricExceptWith([2, 3]));

        Assert.True(set.SetEquals([1, 2]));
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Dictionary_Add_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var dictionary = new DurableDictionary<int, int>("dictionary", new TestJournalManager(writer), new ThrowingDictionaryCodec<int, int>(throwOnSet: true));

        Assert.Throws<InvalidOperationException>(() => dictionary.Add(1, 1));

        Assert.Empty(dictionary);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Dictionary_Set_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var dictionary = new DurableDictionary<int, int>("dictionary", new TestJournalManager(writer), new ThrowingDictionaryCodec<int, int>(throwOnSet: true));
        ((IDurableDictionaryOperationHandler<int, int>)dictionary).ApplySet(1, 1);

        Assert.Throws<InvalidOperationException>(() => dictionary[1] = 2);

        Assert.Equal(1, dictionary[1]);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Dictionary_Remove_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var dictionary = new DurableDictionary<int, int>("dictionary", new TestJournalManager(writer), new ThrowingDictionaryCodec<int, int>(throwOnRemove: true));
        ((IDurableDictionaryOperationHandler<int, int>)dictionary).ApplySet(1, 1);

        Assert.Throws<InvalidOperationException>(() => dictionary.Remove(1));

        Assert.Contains(1, dictionary.Keys);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Dictionary_Clear_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestJournalStreamWriter();
        var dictionary = new DurableDictionary<int, int>("dictionary", new TestJournalManager(writer), new ThrowingDictionaryCodec<int, int>(throwOnClear: true));
        ((IDurableDictionaryOperationHandler<int, int>)dictionary).ApplySet(1, 1);
        ((IDurableDictionaryOperationHandler<int, int>)dictionary).ApplySet(2, 2);

        Assert.Throws<InvalidOperationException>(dictionary.Clear);

        Assert.Equal(2, dictionary.Count);
        Assert.Equal(1, dictionary[1]);
        Assert.Equal(2, dictionary[2]);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Collections_UseDirectEntryWriter()
    {
        var writer = new TestJournalStreamWriter();
        var manager = new TestJournalManager(writer);
        var queue = new DurableQueue<int>("queue", manager, new DirectQueueCodec<int>());
        var set = new DurableSet<int>("set", manager, new DirectSetCodec<int>());
        var dictionary = new DurableDictionary<int, int>("dictionary", manager, new DirectDictionaryCodec<int, int>());

        queue.Enqueue(1);
        set.Add(2);
        dictionary.Add(3, 4);

        Assert.Single(queue);
        Assert.Contains(2, (IEnumerable<int>)set);
        Assert.Equal(4, dictionary[3]);
        Assert.True(writer.Length > 0);
    }

    private sealed class TestJournalManager(TestJournalStreamWriter writer) : IStateManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;

        public void RegisterState(string name, IJournaledState state) => state.Reset(writer.CreateWriter());

        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state)
        {
            state = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class TestJournalStreamWriter
    {
        private readonly OrleansBinaryJournalBatchWriter _buffer = new();

        public long Length => _buffer.Length;

        public JournalStreamWriter CreateWriter() => _buffer.CreateJournalStreamWriter(new JournalStreamId(1));
    }

    private sealed class DirectQueueCodec<T> : TestQueueCodec<T>
    {
        public override void WriteEnqueue(T item, JournalStreamWriter writer) => WriteCommittedByte(writer);

        public override void WriteDequeue(JournalStreamWriter writer) => WriteCommittedByte(writer);
    }

    private sealed class ThrowingQueueCodec<T>(bool throwOnEnqueue = false, bool throwOnDequeue = false, bool throwOnClear = false) : TestQueueCodec<T>
    {
        public override void WriteEnqueue(T item, JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            if (throwOnEnqueue)
            {
                throw new InvalidOperationException("Expected test exception.");
            }

            entry.Commit();
        }

        public override void WriteDequeue(JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            if (throwOnDequeue)
            {
                throw new InvalidOperationException("Expected test exception.");
            }

            entry.Commit();
        }

        public override void WriteClear(JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            if (throwOnClear)
            {
                throw new InvalidOperationException("Expected test exception.");
            }

            entry.Commit();
        }
    }

    private abstract class TestQueueCodec<T> : IDurableQueueOperationCodec<T>
    {
        public virtual void WriteEnqueue(T item, JournalStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteDequeue(JournalStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteClear(JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableQueueOperationHandler<T> consumer) => throw new NotSupportedException();
    }

    private sealed class DirectSetCodec<T> : TestSetCodec<T>
    {
        public override void WriteAdd(T item, JournalStreamWriter writer) => WriteCommittedByte(writer);

        public override void WriteRemove(T item, JournalStreamWriter writer) => WriteCommittedByte(writer);
    }

    private sealed class ThrowingSetCodec<T>(bool throwOnAdd = false, bool throwOnRemove = false, bool throwOnClear = false, bool throwOnSnapshot = false) : TestSetCodec<T>
    {
        public override void WriteAdd(T item, JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            if (throwOnAdd)
            {
                throw new InvalidOperationException("Expected test exception.");
            }

            entry.Commit();
        }

        public override void WriteRemove(T item, JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            if (throwOnRemove)
            {
                throw new InvalidOperationException("Expected test exception.");
            }

            entry.Commit();
        }

        public override void WriteClear(JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            if (throwOnClear)
            {
                throw new InvalidOperationException("Expected test exception.");
            }

            entry.Commit();
        }

        public override void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            if (throwOnSnapshot)
            {
                throw new InvalidOperationException("Expected test exception.");
            }

            entry.Commit();
        }
    }

    private abstract class TestSetCodec<T> : IDurableSetOperationCodec<T>
    {
        public virtual void WriteAdd(T item, JournalStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteRemove(T item, JournalStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteClear(JournalStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableSetOperationHandler<T> consumer) => throw new NotSupportedException();
    }

    private sealed class DirectDictionaryCodec<TKey, TValue> : TestDictionaryCodec<TKey, TValue> where TKey : notnull
    {
        public override void WriteSet(TKey key, TValue value, JournalStreamWriter writer) => WriteCommittedByte(writer);

        public override void WriteRemove(TKey key, JournalStreamWriter writer) => WriteCommittedByte(writer);
    }

    private sealed class ThrowingDictionaryCodec<TKey, TValue>(bool throwOnSet = false, bool throwOnRemove = false, bool throwOnClear = false) : TestDictionaryCodec<TKey, TValue> where TKey : notnull
    {
        public override void WriteSet(TKey key, TValue value, JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            if (throwOnSet)
            {
                throw new InvalidOperationException("Expected test exception.");
            }

            entry.Commit();
        }

        public override void WriteRemove(TKey key, JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            if (throwOnRemove)
            {
                throw new InvalidOperationException("Expected test exception.");
            }

            entry.Commit();
        }

        public override void WriteClear(JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            if (throwOnClear)
            {
                throw new InvalidOperationException("Expected test exception.");
            }

            entry.Commit();
        }
    }

    private abstract class TestDictionaryCodec<TKey, TValue> : IDurableDictionaryOperationCodec<TKey, TValue> where TKey : notnull
    {
        public virtual void WriteSet(TKey key, TValue value, JournalStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteRemove(TKey key, JournalStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteClear(JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryOperationHandler<TKey, TValue> consumer) => throw new NotSupportedException();
    }

    private static void WriteCommittedByte(JournalStreamWriter writer)
    {
        using var entry = writer.BeginEntry();
        WriteByte(entry.Writer);
        entry.Commit();
    }

    private static void WriteByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = 1;
        output.Advance(1);
    }
}
