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
        var writer = new TestLogWriter();
        var list = new DurableList<int>("list", new TestLogManager(writer), new ThrowingAddCodec<int>());

        var thrown = false;
        try
        {
            list.Add(1);
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }

        Assert.True(thrown);
        Assert.Empty(list);
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void Add_UsesDirectEntryWriter()
    {
        var writer = new TestLogWriter();
        var list = new DurableList<int>("list", new TestLogManager(writer), new DirectAddCodec<int>());

        list.Add(1);

        Assert.Single(list);
        Assert.Equal(1, list[0]);
        Assert.True(writer.Length > 0);
    }

    private sealed class TestLogManager(ILogWriter writer) : ILogManager
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

    private sealed class TestLogWriter : ILogWriter
    {
        private readonly LogSegmentBuffer _buffer = new();

        public long Length => _buffer.Length;

        public LogEntry BeginEntry() => _buffer.CreateLogWriter(new LogStreamId(1)).BeginEntry();
    }

    private sealed class ThrowingAddCodec<T> : TestListCodec<T>
    {
        public override void WriteAdd(T item, IBufferWriter<byte> output)
        {
            var span = output.GetSpan(1);
            span[0] = 1;
            output.Advance(1);
            throw new InvalidOperationException("Expected test exception.");
        }
    }

    private sealed class DirectAddCodec<T> : TestListCodec<T>
    {
        public override void WriteAdd(T item, IBufferWriter<byte> output)
        {
            var span = output.GetSpan(1);
            span[0] = 1;
            output.Advance(1);
        }
    }

    private abstract class TestListCodec<T> : IDurableListOperationCodec<T>
    {
        public abstract void WriteAdd(T item, IBufferWriter<byte> output);

        public void WriteSet(int index, T item, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteInsert(int index, T item, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteRemoveAt(int index, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteClear(IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableListOperationHandler<T> consumer) => throw new NotSupportedException();
    }
}
