using BenchmarkDotNet.Attributes;
using Orleans.Journaling;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Benchmarks.Journaling;

[BenchmarkCategory("Journaling", "CommandReaders")]
[MemoryDiagnoser(displayGenColumns: false)]
public class DurableCommandReaderBenchmarks
{
    private const int SmallOperationCount = 4_096;
    private const int SmallItemCount = SmallOperationCount / 2;
    private const int SnapshotItemCount = 16_384;
    private static readonly JournalStreamId ListJournalStreamId = new(8);
    private static readonly int[] SnapshotItems = Enumerable.Range(0, SnapshotItemCount).ToArray();

    private IJournalFormat _journalFormat;
    private IDurableListCommandCodec<int> _codec;
    private EncodedJournalData _smallOperations;
    private EncodedJournalData _snapshotOperation;
    private ArcBufferWriter _readBuffer;
    private ListReplayConsumer _consumer;
    private JournalReplayContext _replayContext;

    [Params(CodecFamily.OrleansBinary)]
    public CodecFamily Family { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var codecFamily = CreateCodecFamily(Family);
        _journalFormat = codecFamily.JournalFormat;
        _codec = codecFamily.Codec;
        _readBuffer = new ArcBufferWriter();
        _consumer = new ListReplayConsumer(_codec, SnapshotItemCount);
        _replayContext = JournalReplayContextFactory.Create(OrleansBinaryJournalFormat.JournalFormatKey, ListJournalStreamId, _consumer);
        _smallOperations = CreateSmallOperations(_journalFormat, _codec);
        _snapshotOperation = CreateSnapshotOperation(_journalFormat, _codec);

        ValidateEncodedJournalData(_smallOperations, SmallItemCount);
        ValidateEncodedJournalData(_snapshotOperation, SnapshotItemCount);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _snapshotOperation?.Dispose();
        _smallOperations?.Dispose();
        _readBuffer?.Dispose();
    }

    [Benchmark(OperationsPerInvoke = SmallOperationCount)]
    public long ReadAndApplySmallOperations()
    {
        Replay(_smallOperations);
        return _consumer.Result;
    }

    [Benchmark(OperationsPerInvoke = SnapshotItemCount)]
    public long ReadAndApplyLargeSnapshot()
    {
        Replay(_snapshotOperation);
        return _consumer.Result;
    }

    private void Replay(EncodedJournalData data)
    {
        _consumer.ResetForReplay();
        _readBuffer.Reset();
        _readBuffer.Write(data.Buffer.AsReadOnlySequence());
        var reader = new JournalBufferReader(_readBuffer.Reader, isCompleted: true);
        _journalFormat.Replay(reader, _replayContext);
    }

    private void ValidateEncodedJournalData(EncodedJournalData data, int expectedCount)
    {
        Replay(data);
        if (_consumer.Count != expectedCount)
        {
            throw new InvalidOperationException($"The encoded {Family} benchmark data replayed {_consumer.Count} item(s), expected {expectedCount}.");
        }
    }

    private static EncodedJournalData CreateSmallOperations(IJournalFormat journalFormat, IDurableListCommandCodec<int> codec)
    {
        var writer = journalFormat.CreateWriter();
        var streamWriter = writer.CreateJournalStreamWriter(ListJournalStreamId);
        for (var i = 0; i < SmallItemCount; i++)
        {
            codec.WriteAdd(i, streamWriter);
        }

        for (var i = 0; i < SmallItemCount; i++)
        {
            codec.WriteSet(i, -i, streamWriter);
        }

        return new EncodedJournalData(writer);
    }

    private static EncodedJournalData CreateSnapshotOperation(IJournalFormat journalFormat, IDurableListCommandCodec<int> codec)
    {
        var writer = journalFormat.CreateWriter();
        codec.WriteSnapshot(SnapshotItems, writer.CreateJournalStreamWriter(ListJournalStreamId));
        return new EncodedJournalData(writer);
    }

    private static CodecFamilyServices CreateCodecFamily(CodecFamily family)
    {
        return family switch
        {
            CodecFamily.OrleansBinary => CreateOrleansBinaryFamily(),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unsupported journaling codec family.")
        };
    }

    private static CodecFamilyServices CreateOrleansBinaryFamily()
    {
        var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        var codecProvider = serviceProvider.GetRequiredService<ICodecProvider>();
        return new CodecFamilyServices(
            new OrleansBinaryJournalFormat(sessionPool),
            new OrleansBinaryDurableListCommandCodec<int>(codecProvider.GetCodec<int>(), sessionPool));
    }

    public enum CodecFamily
    {
        OrleansBinary
    }

    private sealed class CodecFamilyServices(IJournalFormat journalFormat, IDurableListCommandCodec<int> codec)
    {
        public IJournalFormat JournalFormat { get; } = journalFormat;
        public IDurableListCommandCodec<int> Codec { get; } = codec;
    }

    private sealed class EncodedJournalData : IDisposable
    {
        private readonly JournalBufferWriter _writer;

        public EncodedJournalData(JournalBufferWriter writer)
        {
            _writer = writer;
            Buffer = writer.GetBuffer();
        }

        public ArcBuffer Buffer;

        public void Dispose()
        {
            Buffer.Dispose();
            _writer.Dispose();
        }
    }

    private sealed class ListReplayConsumer(
        IDurableListCommandCodec<int> codec,
        int capacity) : IJournaledState, IDurableListCommandHandler<int>
    {
        private readonly List<int> _items = new(capacity);
        private long _checksum;

        public int Count => _items.Count;

        public long Result => _checksum ^ _items.Count;

        public void ResetForReplay()
        {
            _items.Clear();
            _checksum = 0;
        }

        void IJournaledState.Reset(JournalStreamWriter storage) => ResetForReplay();

        void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, codec).Apply(entry.Reader, this);

        void IJournaledState.AppendEntries(JournalStreamWriter writer)
        {
        }

        void IJournaledState.AppendSnapshot(JournalStreamWriter writer)
        {
        }

        IJournaledState IJournaledState.DeepCopy() => throw new NotSupportedException();

        public void ApplyAdd(int item)
        {
            _items.Add(item);
            _checksum += item;
        }

        public void ApplySet(int index, int item)
        {
            _checksum -= _items[index];
            _items[index] = item;
            _checksum += item;
        }

        public void ApplyInsert(int index, int item)
        {
            _items.Insert(index, item);
            _checksum += item;
        }

        public void ApplyRemoveAt(int index)
        {
            _checksum -= _items[index];
            _items.RemoveAt(index);
        }

        public void ApplyClear()
        {
            _items.Clear();
            _checksum = 0;
        }

        public void Reset(int capacityHint)
        {
            _items.Clear();
            _items.EnsureCapacity(capacityHint);
            _checksum = 0;
        }
    }

}
