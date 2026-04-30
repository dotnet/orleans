using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Orleans.Journaling;
using Orleans.Serialization.Buffers;

namespace Benchmarks.Journaling;

[BenchmarkCategory("Journaling")]
[MemoryDiagnoser(displayGenColumns: false)]
public class DurableListJournalBenchmarks
{
    private const int OperationsPerInvocation = 4_096;
    private static readonly LogStreamId ListLogStreamId = new(8);

    private IDurableListOperationCodec<int> _codec;
    private ILogFormat _logFormat;
    private DurableList<int> _list;
    private IDurableStateMachine _stateMachine;
    private LogSegmentBuffer _writeBuffer;
    private BenchmarkLogWriter _outOfBandWriter;
    private LogSegmentBuffer _encodedLogWriter;
    private ArcBuffer _encodedLogData;
    private ArcBufferWriter _recoveryBuffer;
    private RecoveryConsumer _recoveryConsumer;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _codec = new OrleansBinaryListOperationCodec<int>(RawInt32LogValueCodec.Instance);
        _logFormat = OrleansBinaryLogFormat.Instance;
        _writeBuffer = new LogSegmentBuffer();
        _outOfBandWriter = new BenchmarkLogWriter(_writeBuffer, ListLogStreamId);
        _list = new DurableList<int>("list", new BenchmarkLogManager(_outOfBandWriter), _codec);
        _stateMachine = _list;
        _recoveryConsumer = new RecoveryConsumer(ListLogStreamId, _codec, OperationsPerInvocation);

        WarmWritePathCapacity();
        _encodedLogData = CreateEncodedLogData();
        _recoveryBuffer = new ArcBufferWriter();
        ValidateEncodedLogData();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _encodedLogData.Dispose();
        _recoveryBuffer.Dispose();
        _encodedLogWriter.Dispose();
        _writeBuffer.Dispose();
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int DurableListAddWritesDirectEntry()
    {
        ResetWritePath();
        for (var i = 0; i < OperationsPerInvocation; i++)
        {
            _list.Add(i);
        }

        return _list.Count;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int RecoverEncodedLogData()
    {
        _recoveryConsumer.Reset();
        ReplayEncodedLogData();

        return _recoveryConsumer.Count;
    }

    private void WarmWritePathCapacity()
    {
        for (var i = 0; i < OperationsPerInvocation; i++)
        {
            _list.Add(i);
        }

        ResetWritePath();
    }

    private void ResetWritePath()
    {
        _writeBuffer.Reset();
        _stateMachine.Reset(_outOfBandWriter);
    }

    private ArcBuffer CreateEncodedLogData()
    {
        _encodedLogWriter = new LogSegmentBuffer();
        var writer = _encodedLogWriter.CreateLogWriter(ListLogStreamId);
        for (var i = 0; i < OperationsPerInvocation; i++)
        {
            using var entry = writer.BeginEntry();
            _codec.WriteAdd(i, entry.Writer);
            entry.Commit();
        }

        return _encodedLogWriter.GetCommittedBuffer();
    }

    private void ValidateEncodedLogData()
    {
        ReplayEncodedLogData();
        if (_recoveryConsumer.Count != OperationsPerInvocation)
        {
            throw new InvalidOperationException("The encoded journaling benchmark data did not replay all operations.");
        }

        _recoveryConsumer.Reset();
    }

    private void ReplayEncodedLogData()
    {
        _recoveryBuffer.Reset();
        _recoveryBuffer.Write(_encodedLogData.AsReadOnlySequence());
        var reader = new ArcBufferReader(_recoveryBuffer);
        while (_logFormat.TryRead(reader, _recoveryConsumer, isCompleted: true))
        {
        }
    }

    private sealed class RawInt32LogValueCodec : ILogValueCodec<int>
    {
        public static RawInt32LogValueCodec Instance { get; } = new();

        public void Write(int value, IBufferWriter<byte> output)
        {
            var span = output.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            output.Advance(sizeof(int));
        }

        public int Read(ReadOnlySequence<byte> input, out long bytesConsumed)
        {
            var reader = new SequenceReader<byte>(input);
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            if (reader.Remaining < sizeof(int) || !reader.TryCopyTo(bytes))
            {
                throw new InvalidOperationException("The encoded integer payload is truncated.");
            }

            reader.Advance(sizeof(int));
            bytesConsumed = reader.Consumed;
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }
    }

    private sealed class BenchmarkLogWriter(LogSegmentBuffer buffer, LogStreamId streamId) : ILogWriter
    {
        public LogEntry BeginEntry() => buffer.CreateLogWriter(streamId).BeginEntry();
    }

    private sealed class BenchmarkLogManager(ILogWriter writer) : ILogManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;

        public void RegisterStateMachine(string name, IDurableStateMachine stateMachine) => stateMachine.Reset(writer);

        public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IDurableStateMachine stateMachine)
        {
            stateMachine = null!;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class RecoveryConsumer(
        LogStreamId expectedStreamId,
        IDurableListOperationCodec<int> codec,
        int capacity) : ILogStreamStateMachineResolver, IDurableStateMachine, IDurableListOperationHandler<int>
    {
        private readonly List<int> _items = new(capacity);

        public int Count => _items.Count;

        public void Reset() => _items.Clear();

        object IDurableStateMachine.OperationCodec => codec;

        public IDurableStateMachine ResolveStateMachine(LogStreamId streamId)
        {
            if (streamId != expectedStreamId)
            {
                throw new InvalidOperationException("The encoded journaling benchmark data contained an unexpected stream id.");
            }

            return this;
        }

        public void Apply(ReadOnlySequence<byte> payload)
        {
            codec.Apply(payload, this);
        }

        void IDurableStateMachine.Reset(ILogWriter storage) => Reset();
        void IDurableStateMachine.AppendEntries(LogWriter writer) { }
        void IDurableStateMachine.AppendSnapshot(LogWriter writer) { }
        IDurableStateMachine IDurableStateMachine.DeepCopy() => throw new NotSupportedException();

        public void ApplyAdd(int item) => _items.Add(item);

        public void ApplySet(int index, int item) => _items[index] = item;

        public void ApplyInsert(int index, int item) => _items.Insert(index, item);

        public void ApplyRemoveAt(int index) => _items.RemoveAt(index);

        public void ApplyClear() => _items.Clear();

        public void ApplySnapshotStart(int count)
        {
            _items.Clear();
            _items.EnsureCapacity(count);
        }

        public void ApplySnapshotItem(int item) => _items.Add(item);
    }
}
