using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class DurableListDirectWriteTests
{
    [Fact]
    public void Add_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestLogStreamWriter();
        var list = new DurableList<int>("list", new TestLogManager(writer), new ThrowingListCodec<int>(throwOnAdd: true));

        Assert.Throws<InvalidOperationException>(() => list.Add(1));

        Assert.Empty(list);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Set_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestLogStreamWriter();
        var list = new DurableList<int>("list", new TestLogManager(writer), new ThrowingListCodec<int>(throwOnSet: true));
        ((IDurableListOperationHandler<int>)list).ApplyAdd(1);
        ((IDurableListOperationHandler<int>)list).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(() => list[0] = 10);

        Assert.Equal([1, 2], list);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Insert_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestLogStreamWriter();
        var list = new DurableList<int>("list", new TestLogManager(writer), new ThrowingListCodec<int>(throwOnInsert: true));
        ((IDurableListOperationHandler<int>)list).ApplyAdd(1);
        ((IDurableListOperationHandler<int>)list).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(() => list.Insert(1, 10));

        Assert.Equal([1, 2], list);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void RemoveAt_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestLogStreamWriter();
        var list = new DurableList<int>("list", new TestLogManager(writer), new ThrowingListCodec<int>(throwOnRemoveAt: true));
        ((IDurableListOperationHandler<int>)list).ApplyAdd(1);
        ((IDurableListOperationHandler<int>)list).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(() => list.RemoveAt(0));

        Assert.Equal([1, 2], list);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Clear_DoesNotMutateWhenEncodingFails()
    {
        var writer = new TestLogStreamWriter();
        var list = new DurableList<int>("list", new TestLogManager(writer), new ThrowingListCodec<int>(throwOnClear: true));
        ((IDurableListOperationHandler<int>)list).ApplyAdd(1);
        ((IDurableListOperationHandler<int>)list).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(list.Clear);

        Assert.Equal([1, 2], list);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Add_UsesDirectEntryWriter()
    {
        var writer = new TestLogStreamWriter();
        var list = new DurableList<int>("list", new TestLogManager(writer), new DirectAddCodec<int>());

        list.Add(1);

        Assert.Single(list);
        Assert.Equal(1, list[0]);
        Assert.True(writer.Length > 0);
    }

    private sealed class TestLogManager(TestLogStreamWriter writer) : IStateMachineManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;

        public void RegisterStateMachine(string name, IDurableStateMachine stateMachine) => stateMachine.Reset(writer.CreateWriter());

        public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IDurableStateMachine? stateMachine)
        {
            stateMachine = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class TestLogStreamWriter
    {
        private readonly OrleansBinaryLogBatchWriter _buffer = new();

        public long Length => _buffer.Length;

        public LogStreamWriter CreateWriter() => _buffer.CreateLogStreamWriter(new LogStreamId(1));
    }

    private sealed class ThrowingListCodec<T>(
        bool throwOnAdd = false,
        bool throwOnSet = false,
        bool throwOnInsert = false,
        bool throwOnRemoveAt = false,
        bool throwOnClear = false) : TestListCodec<T>
    {
        public override void WriteAdd(T item, LogStreamWriter writer)
        {
            WriteOrThrow(writer, throwOnAdd);
        }

        public override void WriteSet(int index, T item, LogStreamWriter writer)
        {
            WriteOrThrow(writer, throwOnSet);
        }

        public override void WriteInsert(int index, T item, LogStreamWriter writer)
        {
            WriteOrThrow(writer, throwOnInsert);
        }

        public override void WriteRemoveAt(int index, LogStreamWriter writer)
        {
            WriteOrThrow(writer, throwOnRemoveAt);
        }

        public override void WriteClear(LogStreamWriter writer)
        {
            WriteOrThrow(writer, throwOnClear);
        }
    }

    private sealed class DirectAddCodec<T> : TestListCodec<T>
    {
        public override void WriteAdd(T item, LogStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            entry.Commit();
        }
    }

    private abstract class TestListCodec<T> : IDurableListOperationCodec<T>
    {
        public virtual void WriteAdd(T item, LogStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteSet(int index, T item, LogStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteInsert(int index, T item, LogStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteRemoveAt(int index, LogStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteClear(LogStreamWriter writer) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<T> items, LogStreamWriter writer) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableListOperationHandler<T> consumer) => throw new NotSupportedException();
    }

    private static void WriteOrThrow(LogStreamWriter writer, bool shouldThrow)
    {
        using var entry = writer.BeginEntry();
        WriteByte(entry.Writer);
        if (shouldThrow)
        {
            throw new InvalidOperationException("Expected test exception.");
        }

        entry.Commit();
    }

    private static void WriteByte(IBufferWriter<byte> output)
    {
        var span = output.GetSpan(1);
        span[0] = 1;
        output.Advance(1);
    }
}
