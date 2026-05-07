using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.MessagePack;
using Orleans.Journaling.Protobuf;
using Orleans.Serialization.Buffers;

namespace Benchmarks.Journaling;

[BenchmarkCategory("Journaling", "OperationReaders")]
[MemoryDiagnoser(displayGenColumns: false)]
public class DurableOperationReaderBenchmarks
{
    private const int SmallOperationCount = 4_096;
    private const int SmallItemCount = SmallOperationCount / 2;
    private const int SnapshotItemCount = 16_384;
    private static readonly LogStreamId ListLogStreamId = new(8);
    private static readonly int[] SnapshotItems = Enumerable.Range(0, SnapshotItemCount).ToArray();

    private ILogFormat _logFormat;
    private IDurableListOperationCodec<int> _codec;
    private EncodedLogData _smallOperations;
    private EncodedLogData _snapshotOperation;
    private ArcBufferWriter _readBuffer;
    private ListReplayConsumer _consumer;
    private IDisposable _services;

    [Params(CodecFamily.OrleansBinary, CodecFamily.MessagePack, CodecFamily.Protobuf)]
    public CodecFamily Family { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var codecFamily = CreateCodecFamily(Family);
        _logFormat = codecFamily.LogFormat;
        _codec = codecFamily.Codec;
        _services = codecFamily.Services;
        _readBuffer = new ArcBufferWriter();
        _consumer = new ListReplayConsumer(ListLogStreamId, _codec, SnapshotItemCount);
        _smallOperations = CreateSmallOperations(_logFormat, _codec);
        _snapshotOperation = CreateSnapshotOperation(_logFormat, _codec);

        ValidateEncodedLogData(_smallOperations, SmallItemCount);
        ValidateEncodedLogData(_snapshotOperation, SnapshotItemCount);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _snapshotOperation?.Dispose();
        _smallOperations?.Dispose();
        _readBuffer?.Dispose();
        _services?.Dispose();
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

    private void Replay(EncodedLogData data)
    {
        _consumer.ResetForReplay();
        _readBuffer.Reset();
        _readBuffer.Write(data.Buffer.AsReadOnlySequence());
        var reader = new LogReadBuffer(new ArcBufferReader(_readBuffer), isCompleted: true);
        _logFormat.Read(reader, _consumer);
    }

    private void ValidateEncodedLogData(EncodedLogData data, int expectedCount)
    {
        Replay(data);
        if (_consumer.Count != expectedCount)
        {
            throw new InvalidOperationException($"The encoded {Family} benchmark data replayed {_consumer.Count} item(s), expected {expectedCount}.");
        }
    }

    private static EncodedLogData CreateSmallOperations(ILogFormat logFormat, IDurableListOperationCodec<int> codec)
    {
        var writer = logFormat.CreateWriter();
        var streamWriter = writer.CreateLogStreamWriter(ListLogStreamId);
        for (var i = 0; i < SmallItemCount; i++)
        {
            codec.WriteAdd(i, streamWriter);
        }

        for (var i = 0; i < SmallItemCount; i++)
        {
            codec.WriteSet(i, -i, streamWriter);
        }

        return new EncodedLogData(writer);
    }

    private static EncodedLogData CreateSnapshotOperation(ILogFormat logFormat, IDurableListOperationCodec<int> codec)
    {
        var writer = logFormat.CreateWriter();
        codec.WriteSnapshot(SnapshotItems, writer.CreateLogStreamWriter(ListLogStreamId));
        return new EncodedLogData(writer);
    }

    private static CodecFamilyServices CreateCodecFamily(CodecFamily family)
    {
        return family switch
        {
            CodecFamily.OrleansBinary => new CodecFamilyServices(
                OrleansBinaryLogFormat.Instance,
                new OrleansBinaryListOperationCodec<int>(RawInt32LogValueCodec.Instance),
                services: null),
            CodecFamily.MessagePack => CreateRegisteredCodecFamily(MessagePackJournalingExtensions.LogFormatKey, static builder => builder.UseMessagePackJournalingFormat()),
            CodecFamily.Protobuf => CreateRegisteredCodecFamily(ProtobufJournalingExtensions.LogFormatKey, static builder => builder.UseProtobufJournalingFormat()),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unsupported journaling codec family.")
        };
    }

    private static CodecFamilyServices CreateRegisteredCodecFamily(string logFormatKey, Action<ISiloBuilder> configure)
    {
        var builder = new BenchmarkSiloBuilder();
        configure(builder);
        var services = builder.Services.BuildServiceProvider();
        var logFormat = services.GetRequiredKeyedService<ILogFormat>(logFormatKey);
        var codec = services.GetRequiredKeyedService<IDurableListOperationCodecProvider>(logFormatKey).GetCodec<int>();
        return new CodecFamilyServices(logFormat, codec, services);
    }

    public enum CodecFamily
    {
        OrleansBinary,
        MessagePack,
        Protobuf
    }

    private sealed class CodecFamilyServices(ILogFormat logFormat, IDurableListOperationCodec<int> codec, IDisposable services)
    {
        public ILogFormat LogFormat { get; } = logFormat;
        public IDurableListOperationCodec<int> Codec { get; } = codec;
        public IDisposable Services { get; } = services;
    }

    private sealed class EncodedLogData : IDisposable
    {
        private readonly ILogBatchWriter _writer;

        public EncodedLogData(ILogBatchWriter writer)
        {
            _writer = writer;
            Buffer = writer.GetCommittedBuffer();
        }

        public ArcBuffer Buffer;

        public void Dispose()
        {
            Buffer.Dispose();
            _writer.Dispose();
        }
    }

    private sealed class ListReplayConsumer(
        LogStreamId expectedStreamId,
        IDurableListOperationCodec<int> codec,
        int capacity) : IStateMachineResolver, IDurableStateMachine, IDurableListOperationHandler<int>
    {
        private readonly List<int> _items = new(capacity);
        private long _checksum;

        public int Count => _items.Count;

        public long Result => _checksum ^ _items.Count;

        object IDurableStateMachine.OperationCodec => codec;

        public IDurableStateMachine ResolveStateMachine(LogStreamId streamId)
        {
            if (streamId != expectedStreamId)
            {
                throw new InvalidOperationException($"The benchmark log data contained unexpected stream id {streamId.Value}.");
            }

            return this;
        }

        public void ResetForReplay()
        {
            _items.Clear();
            _checksum = 0;
        }

        public void Apply(ReadOnlySequence<byte> payload) => codec.Apply(payload, this);

        void IDurableStateMachine.Reset(LogStreamWriter storage) => ResetForReplay();

        void IDurableStateMachine.AppendEntries(LogStreamWriter writer)
        {
        }

        void IDurableStateMachine.AppendSnapshot(LogStreamWriter writer)
        {
        }

        IDurableStateMachine IDurableStateMachine.DeepCopy() => throw new NotSupportedException();

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

    private sealed class BenchmarkSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
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
}
