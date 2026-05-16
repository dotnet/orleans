using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Orleans.Journaling;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Benchmarks.Journaling;

[BenchmarkCategory("Journaling")]
[MemoryDiagnoser(displayGenColumns: false)]
public class DurableListJournalBenchmarks
{
    private const int OperationsPerInvocation = 4_096;
    private static readonly JournalStreamId ListJournalStreamId = new(8);

    private IDurableListCommandCodec<int> _codec;
    private IJournalFormat _journalFormat;
    private DurableList<int> _list;
    private IJournaledState _state;
    private OrleansBinaryJournalBufferWriter _writeBuffer;
    private OrleansBinaryJournalBufferWriter _encodedJournalStreamWriter;
    private ArcBuffer _encodedJournalData;
    private ArcBufferWriter _recoveryBuffer;
    private RecoveryConsumer _recoveryConsumer;
    private JournalReplayContext _replayContext;
    private SerializerSessionPool _sessionPool;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        _sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        _codec = new OrleansBinaryDurableListCommandCodec<int>(serviceProvider.GetRequiredService<ICodecProvider>().GetCodec<int>(), _sessionPool);
        _journalFormat = new OrleansBinaryJournalFormat(_sessionPool);
        _writeBuffer = new OrleansBinaryJournalBufferWriter();
        _list = new DurableList<int>("list", new BenchmarkJournalManager(_writeBuffer, ListJournalStreamId), _codec);
        _state = _list;
        _recoveryConsumer = new RecoveryConsumer(_codec, OperationsPerInvocation);
        _replayContext = JournalReplayContextFactory.Create(OrleansBinaryJournalFormat.JournalFormatKey, ListJournalStreamId, _recoveryConsumer);

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
        _encodedJournalStreamWriter = new OrleansBinaryJournalBufferWriter();
        var writer = _encodedJournalStreamWriter.CreateJournalStreamWriter(ListJournalStreamId);
        for (var i = 0; i < OperationsPerInvocation; i++)
        {
            _codec.WriteAdd(i, writer);
        }

        return _encodedJournalStreamWriter.GetBuffer();
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
        var reader = new JournalBufferReader(_recoveryBuffer.Reader, isCompleted: true);
        _journalFormat.Replay(reader, _replayContext);
    }

    private sealed class BenchmarkJournalManager(OrleansBinaryJournalBufferWriter buffer, JournalStreamId streamId) : IJournaledStateManager
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
        IDurableListCommandCodec<int> codec,
        int capacity) : IJournaledState, IDurableListCommandHandler<int>
    {
        private readonly List<int> _items = new(capacity);

        public int Count => _items.Count;

        public void Reset() => _items.Clear();

        void IJournaledState.Reset(JournalStreamWriter storage) => Reset();

        void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, codec).Apply(entry.Reader, this);

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
