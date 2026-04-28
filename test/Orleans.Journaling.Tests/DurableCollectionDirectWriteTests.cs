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
        var writer = new TestLogWriter();
        var queue = new DurableQueue<int>("queue", new TestStateMachineManager(writer), new ThrowingQueueCodec<int>(throwOnEnqueue: true));

        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(1));

        Assert.Empty(queue);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Queue_Dequeue_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestLogWriter();
        var queue = new DurableQueue<int>("queue", new TestStateMachineManager(writer), new ThrowingQueueCodec<int>(throwOnDequeue: true));
        ((IDurableQueueLogEntryConsumer<int>)queue).ApplyEnqueue(1);

        Assert.Throws<InvalidOperationException>(() => queue.Dequeue());

        Assert.Single(queue);
        Assert.Equal(1, queue.Peek());
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Set_Add_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestLogWriter();
        var set = new DurableSet<int>("set", new TestStateMachineManager(writer), new ThrowingSetCodec<int>(throwOnAdd: true));

        Assert.Throws<InvalidOperationException>(() => set.Add(1));

        Assert.Empty(set);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Set_Remove_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestLogWriter();
        var set = new DurableSet<int>("set", new TestStateMachineManager(writer), new ThrowingSetCodec<int>(throwOnRemove: true));
        ((IDurableSetLogEntryConsumer<int>)set).ApplyAdd(1);

        Assert.Throws<InvalidOperationException>(() => set.Remove(1));

        Assert.Contains(1, (IEnumerable<int>)set);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Set_IntersectWith_DoesNotMutateWhenSnapshotEncodingFails()
    {
        var writer = new TestLogWriter();
        var set = new DurableSet<int>("set", new TestStateMachineManager(writer), new ThrowingSetCodec<int>(throwOnSnapshot: true));
        ((IDurableSetLogEntryConsumer<int>)set).ApplyAdd(1);
        ((IDurableSetLogEntryConsumer<int>)set).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(() => set.IntersectWith([2, 3]));

        Assert.Contains(1, (IEnumerable<int>)set);
        Assert.Contains(2, (IEnumerable<int>)set);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Dictionary_Add_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestLogWriter();
        var dictionary = new DurableDictionary<int, int>("dictionary", new TestStateMachineManager(writer), new ThrowingDictionaryCodec<int, int>(throwOnSet: true));

        Assert.Throws<InvalidOperationException>(() => dictionary.Add(1, 1));

        Assert.Empty(dictionary);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Dictionary_Set_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestLogWriter();
        var dictionary = new DurableDictionary<int, int>("dictionary", new TestStateMachineManager(writer), new ThrowingDictionaryCodec<int, int>(throwOnSet: true));
        ((IDurableDictionaryLogEntryConsumer<int, int>)dictionary).ApplySet(1, 1);

        Assert.Throws<InvalidOperationException>(() => dictionary[1] = 2);

        Assert.Equal(1, dictionary[1]);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Dictionary_Remove_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestLogWriter();
        var dictionary = new DurableDictionary<int, int>("dictionary", new TestStateMachineManager(writer), new ThrowingDictionaryCodec<int, int>(throwOnRemove: true));
        ((IDurableDictionaryLogEntryConsumer<int, int>)dictionary).ApplySet(1, 1);

        Assert.Throws<InvalidOperationException>(() => dictionary.Remove(1));

        Assert.Contains(1, dictionary.Keys);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Collections_UseDirectEntryWriter()
    {
        var writer = new TestLogWriter();
        var manager = new TestStateMachineManager(writer);
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

    private sealed class TestStateMachineManager(IStateMachineLogWriter writer) : IStateMachineManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;

        public void RegisterStateMachine(string name, IDurableStateMachine stateMachine) => stateMachine.Reset(writer);

        public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IDurableStateMachine? stateMachine)
        {
            stateMachine = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class TestLogWriter : IStateMachineLogWriter
    {
        private readonly LogExtentBuilder _buffer = new();

        public long Length => _buffer.Length;

        public LogEntryWriter BeginEntry() => _buffer.BeginEntry(new StateMachineId(1));
    }

    private sealed class DirectQueueCodec<T> : TestQueueCodec<T>
    {
        public override void WriteEnqueue(T item, IBufferWriter<byte> output) => WriteByte(output);

        public override void WriteDequeue(IBufferWriter<byte> output) => WriteByte(output);
    }

    private sealed class ThrowingQueueCodec<T>(bool throwOnEnqueue = false, bool throwOnDequeue = false) : TestQueueCodec<T>
    {
        public override void WriteEnqueue(T item, IBufferWriter<byte> output)
        {
            WriteByte(output);
            if (throwOnEnqueue)
            {
                throw new InvalidOperationException("Expected test exception.");
            }
        }

        public override void WriteDequeue(IBufferWriter<byte> output)
        {
            WriteByte(output);
            if (throwOnDequeue)
            {
                throw new InvalidOperationException("Expected test exception.");
            }
        }
    }

    private abstract class TestQueueCodec<T> : IDurableQueueCodec<T>
    {
        public virtual void WriteEnqueue(T item, IBufferWriter<byte> output) => throw new NotSupportedException();

        public virtual void WriteDequeue(IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteClear(IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableQueueLogEntryConsumer<T> consumer) => throw new NotSupportedException();
    }

    private sealed class DirectSetCodec<T> : TestSetCodec<T>
    {
        public override void WriteAdd(T item, IBufferWriter<byte> output) => WriteByte(output);

        public override void WriteRemove(T item, IBufferWriter<byte> output) => WriteByte(output);
    }

    private sealed class ThrowingSetCodec<T>(bool throwOnAdd = false, bool throwOnRemove = false, bool throwOnSnapshot = false) : TestSetCodec<T>
    {
        public override void WriteAdd(T item, IBufferWriter<byte> output)
        {
            WriteByte(output);
            if (throwOnAdd)
            {
                throw new InvalidOperationException("Expected test exception.");
            }
        }

        public override void WriteRemove(T item, IBufferWriter<byte> output)
        {
            WriteByte(output);
            if (throwOnRemove)
            {
                throw new InvalidOperationException("Expected test exception.");
            }
        }

        public override void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output)
        {
            WriteByte(output);
            if (throwOnSnapshot)
            {
                throw new InvalidOperationException("Expected test exception.");
            }
        }
    }

    private abstract class TestSetCodec<T> : IDurableSetCodec<T>
    {
        public virtual void WriteAdd(T item, IBufferWriter<byte> output) => throw new NotSupportedException();

        public virtual void WriteRemove(T item, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteClear(IBufferWriter<byte> output) => throw new NotSupportedException();

        public virtual void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableSetLogEntryConsumer<T> consumer) => throw new NotSupportedException();
    }

    private sealed class DirectDictionaryCodec<TKey, TValue> : TestDictionaryCodec<TKey, TValue> where TKey : notnull
    {
        public override void WriteSet(TKey key, TValue value, IBufferWriter<byte> output) => WriteByte(output);

        public override void WriteRemove(TKey key, IBufferWriter<byte> output) => WriteByte(output);
    }

    private sealed class ThrowingDictionaryCodec<TKey, TValue>(bool throwOnSet = false, bool throwOnRemove = false) : TestDictionaryCodec<TKey, TValue> where TKey : notnull
    {
        public override void WriteSet(TKey key, TValue value, IBufferWriter<byte> output)
        {
            WriteByte(output);
            if (throwOnSet)
            {
                throw new InvalidOperationException("Expected test exception.");
            }
        }

        public override void WriteRemove(TKey key, IBufferWriter<byte> output)
        {
            WriteByte(output);
            if (throwOnRemove)
            {
                throw new InvalidOperationException("Expected test exception.");
            }
        }
    }

    private abstract class TestDictionaryCodec<TKey, TValue> : IDurableDictionaryCodec<TKey, TValue> where TKey : notnull
    {
        public virtual void WriteSet(TKey key, TValue value, IBufferWriter<byte> output) => throw new NotSupportedException();

        public virtual void WriteRemove(TKey key, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteClear(IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer) => throw new NotSupportedException();
    }

    private static void WriteByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = 1;
        output.Advance(1);
    }
}
