using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Orleans.Journaling;
using Orleans.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;

namespace Benchmarks.Journaling;

[BenchmarkCategory("Journaling")]
[MemoryDiagnoser(displayGenColumns: false)]
public class DurableListJournalBenchmarks
{
    private const int OperationsPerInvocation = 4_096;
    private static readonly JournalStreamId ListJournalStreamId = new(8);

    private IDurableListOperationCodec<int> _codec;
    private IJournalFormat _journalFormat;
    private DurableList<int> _list;
    private IJournaledState _state;
    private OrleansBinaryJournalBatchWriter _writeBuffer;
    private OrleansBinaryJournalBatchWriter _encodedJournalStreamWriter;
    private ArcBuffer _encodedJournalData;
    private ArcBufferWriter _recoveryBuffer;
    private RecoveryConsumer _recoveryConsumer;
    private SerializerSessionPool _sessionPool;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sessionPool = new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<SerializerSessionPool>();
        _codec = new OrleansBinaryListOperationCodec<int>(RawInt32JournalValueCodec.Instance, _sessionPool);
        _journalFormat = new OrleansBinaryJournalFormat(_sessionPool);
        _writeBuffer = new OrleansBinaryJournalBatchWriter();
        _list = new DurableList<int>("list", new BenchmarkJournalManager(_writeBuffer, ListJournalStreamId), _codec);
        _state = _list;
        _recoveryConsumer = new RecoveryConsumer(ListJournalStreamId, _codec, OperationsPerInvocation);

        WarmWritePathCapacity();
        _encodedJournalData = CreateEncodedJournalData();
        _recoveryBuffer = new ArcBufferWriter();
        ValidateEncodedJournalData();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _encodedJournalData.Dispose();
        _recoveryBuffer.Dispose();
        _encodedJournalStreamWriter.Dispose();
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
    public int RecoverEncodedJournalData()
    {
        _recoveryConsumer.Reset();
        ReplayEncodedJournalData();

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
        _state.Reset(_writeBuffer.CreateJournalStreamWriter(ListJournalStreamId));
    }

    private ArcBuffer CreateEncodedJournalData()
    {
        _encodedJournalStreamWriter = new OrleansBinaryJournalBatchWriter();
        var writer = _encodedJournalStreamWriter.CreateJournalStreamWriter(ListJournalStreamId);
        for (var i = 0; i < OperationsPerInvocation; i++)
        {
            _codec.WriteAdd(i, writer);
        }

        return _encodedJournalStreamWriter.GetCommittedBuffer();
    }

    private void ValidateEncodedJournalData()
    {
        ReplayEncodedJournalData();
        if (_recoveryConsumer.Count != OperationsPerInvocation)
        {
            throw new InvalidOperationException("The encoded journaling benchmark data did not replay all operations.");
        }

        _recoveryConsumer.Reset();
    }

    private void ReplayEncodedJournalData()
    {
        _recoveryBuffer.Reset();
        _recoveryBuffer.Write(_encodedJournalData.AsReadOnlySequence());
        var reader = new JournalReadBuffer(new ArcBufferReader(_recoveryBuffer), isCompleted: true);
        _journalFormat.Read(reader, _recoveryConsumer);
    }

    private sealed class RawInt32JournalValueCodec : IJournalValueCodec<int>
    {
        public static RawInt32JournalValueCodec Instance { get; } = new();

        public void Write(int value, IBufferWriter<byte> output)
        {
            var span = output.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            output.Advance(sizeof(int));
        }

        public int Read(ref Reader<ArcBufferReaderInput> reader)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            reader.ReadBytes(bytes);
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }
    }

    private sealed class BenchmarkJournalManager(OrleansBinaryJournalBatchWriter buffer, JournalStreamId streamId) : IStateManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;

        public void RegisterState(string name, IJournaledState state) => state.Reset(buffer.CreateJournalStreamWriter(streamId));

        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState state)
        {
            state = null!;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class RecoveryConsumer(
        JournalStreamId expectedStreamId,
        IDurableListOperationCodec<int> codec,
        int capacity) : IStateResolver, IJournaledState, IDurableListOperationHandler<int>
    {
        private readonly List<int> _items = new(capacity);

        public int Count => _items.Count;

        public void Reset() => _items.Clear();

        object IJournaledState.OperationCodec => codec;

        public IJournaledState ResolveState(JournalStreamId streamId)
        {
            if (streamId != expectedStreamId)
            {
                throw new InvalidOperationException("The encoded journaling benchmark data contained an unexpected stream id.");
            }

            return this;
        }

        void IJournaledState.Reset(JournalStreamWriter storage) => Reset();
        void IJournaledState.AppendEntries(JournalStreamWriter writer) { }
        void IJournaledState.AppendSnapshot(JournalStreamWriter writer) { }
        IJournaledState IJournaledState.DeepCopy() => throw new NotSupportedException();

        public void ApplyAdd(int item) => _items.Add(item);

        public void ApplySet(int index, int item) => _items[index] = item;

        public void ApplyInsert(int index, int item) => _items.Insert(index, item);

        public void ApplyRemoveAt(int index) => _items.RemoveAt(index);

        public void ApplyClear() => _items.Clear();

        public void Reset(int capacityHint)
        {
            _items.Clear();
            _items.EnsureCapacity(capacityHint);
        }
    }
}
