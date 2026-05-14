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
        using var writer = new TestJournalStreamWriter();
        var list = new DurableList<int>("list", new TestJournalManager(writer), new ThrowingListCodec<int>(throwOnAdd: true));

        Assert.Throws<InvalidOperationException>(() => list.Add(1));

        Assert.Empty(list);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Set_DoesNotMutateWhenEncodingFails()
    {
        using var writer = new TestJournalStreamWriter();
        var list = new DurableList<int>("list", new TestJournalManager(writer), new ThrowingListCodec<int>(throwOnSet: true));
        ((IDurableListCommandHandler<int>)list).ApplyAdd(1);
        ((IDurableListCommandHandler<int>)list).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(() => list[0] = 10);

        Assert.Equal([1, 2], list);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Insert_DoesNotMutateWhenEncodingFails()
    {
        using var writer = new TestJournalStreamWriter();
        var list = new DurableList<int>("list", new TestJournalManager(writer), new ThrowingListCodec<int>(throwOnInsert: true));
        ((IDurableListCommandHandler<int>)list).ApplyAdd(1);
        ((IDurableListCommandHandler<int>)list).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(() => list.Insert(1, 10));

        Assert.Equal([1, 2], list);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void RemoveAt_DoesNotMutateWhenEncodingFails()
    {
        using var writer = new TestJournalStreamWriter();
        var list = new DurableList<int>("list", new TestJournalManager(writer), new ThrowingListCodec<int>(throwOnRemoveAt: true));
        ((IDurableListCommandHandler<int>)list).ApplyAdd(1);
        ((IDurableListCommandHandler<int>)list).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(() => list.RemoveAt(0));

        Assert.Equal([1, 2], list);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Clear_DoesNotMutateWhenEncodingFails()
    {
        using var writer = new TestJournalStreamWriter();
        var list = new DurableList<int>("list", new TestJournalManager(writer), new ThrowingListCodec<int>(throwOnClear: true));
        ((IDurableListCommandHandler<int>)list).ApplyAdd(1);
        ((IDurableListCommandHandler<int>)list).ApplyAdd(2);

        Assert.Throws<InvalidOperationException>(list.Clear);

        Assert.Equal([1, 2], list);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Add_UsesDirectEntryWriter()
    {
        using var writer = new TestJournalStreamWriter();
        var list = new DurableList<int>("list", new TestJournalManager(writer), new DirectAddCodec<int>());

        list.Add(1);

        Assert.Single(list);
        Assert.Equal(1, list[0]);
        Assert.True(writer.Length > 0);
    }

    private sealed class TestJournalManager(TestJournalStreamWriter writer) : IJournaledStateManager
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

    private sealed class TestJournalStreamWriter : IDisposable
    {
        private readonly OrleansBinaryJournalBufferWriter _buffer = new();

        public long Length
        {
            get
            {
                using var buffer = _buffer.GetBuffer();
                return buffer.Length;
            }
        }

        public JournalStreamWriter CreateWriter() => _buffer.CreateJournalStreamWriter(new JournalStreamId(1));

        public void Dispose() => _buffer.Dispose();
    }

    private sealed class ThrowingListCodec<T>(
        bool throwOnAdd = false,
        bool throwOnSet = false,
        bool throwOnInsert = false,
        bool throwOnRemoveAt = false,
        bool throwOnClear = false) : TestListCodec<T>
    {
        public override void WriteAdd(T item, JournalStreamWriter writer)
        {
            WriteOrThrow(writer, throwOnAdd);
        }

        public override void WriteSet(int index, T item, JournalStreamWriter writer)
        {
            WriteOrThrow(writer, throwOnSet);
        }

        public override void WriteInsert(int index, T item, JournalStreamWriter writer)
        {
            WriteOrThrow(writer, throwOnInsert);
        }

        public override void WriteRemoveAt(int index, JournalStreamWriter writer)
        {
            WriteOrThrow(writer, throwOnRemoveAt);
        }

        public override void WriteClear(JournalStreamWriter writer)
        {
            WriteOrThrow(writer, throwOnClear);
        }
    }

    private sealed class DirectAddCodec<T> : TestListCodec<T>
    {
        public override void WriteAdd(T item, JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            WriteByte(entry.Writer);
            entry.Commit();
        }
    }

    private abstract class TestListCodec<T> : IDurableListCommandCodec<T>
    {
        public virtual void WriteAdd(T item, JournalStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteSet(int index, T item, JournalStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteInsert(int index, T item, JournalStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteRemoveAt(int index, JournalStreamWriter writer) => throw new NotSupportedException();

        public virtual void WriteClear(JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(JournalBufferReader input, IDurableListCommandHandler<T> consumer) => throw new NotSupportedException();
    }

    private static void WriteOrThrow(JournalStreamWriter writer, bool shouldThrow)
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
