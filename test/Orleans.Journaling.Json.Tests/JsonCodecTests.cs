using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Json;
using Orleans.Serialization.Buffers;
using Orleans.Hosting;
using Xunit;

namespace Orleans.Journaling.Json.Tests;

/// <summary>
/// Round-trip and JSON-format tests for the direct JSON codecs.
/// </summary>
[TestCategory("BVT")]
public class JsonCodecTests
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    [Fact]
    public void JsonDictionaryCodec_Set_RoundTrips()
    {
        var codec = new JsonDictionaryOperationCodec<string, int>(Options);
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet("alice", 42, buffer);
        var json = GetString(buffer);
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal("""{"cmd":"set","key":"alice","value":42}""", json);
        Assert.Equal("alice", consumer.LastSetKey);
        Assert.Equal(42, consumer.LastSetValue);
    }

    [Fact]
    public void UseJsonCodec_TypeInfoResolverOverload_RegistersPayloadMetadata()
    {
        var builder = new TestSiloBuilder();
        builder.UseJsonCodec(JsonCodecTestJsonContext.Default);
        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.IsType<JsonLinesLogFormat>(serviceProvider.GetRequiredKeyedService<ILogFormat>(LogFormatKeys.Json));
        var codec = serviceProvider.GetRequiredKeyedService<IDurableValueOperationCodecProvider>(LogFormatKeys.Json).GetCodec<JsonCodecTestValue>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet(new("test", 1), buffer);
        var consumer = new ValueConsumer<JsonCodecTestValue>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(new("test", 1), consumer.Value);
    }

    [Fact]
    public void JsonDictionaryCodec_Snapshot_RoundTrips()
    {
        var codec = new JsonDictionaryOperationCodec<string, int>(Options);
        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
        };
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSnapshot(items, buffer);
        var json = GetString(buffer);
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal("""{"cmd":"snapshot","items":[{"key":"alpha","value":1},{"key":"beta","value":2}]}""", json);
        Assert.Equal(items, consumer.Items);
    }

    [Fact]
    public void JsonListCodec_Operations_RoundTrip()
    {
        var codec = new JsonListOperationCodec<string>(Options);
        var consumer = new ListConsumer<string>();

        Apply(codec, writer => codec.WriteAdd("one", writer), consumer);
        Apply(codec, writer => codec.WriteSet(0, "updated", writer), consumer);
        Apply(codec, writer => codec.WriteInsert(1, "two", writer), consumer);
        Apply(codec, writer => codec.WriteRemoveAt(0, writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);

        Assert.Equal(["add:one", "set:0:updated", "insert:1:two", "remove:0", "clear"], consumer.Commands);
    }

    [Fact]
    public void JsonListCodec_Snapshot_RoundTrips()
    {
        var codec = new JsonListOperationCodec<string>(Options);
        var consumer = new ListConsumer<string>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSnapshot(new[] { "one", "two" }, buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(["snapshot:2", "snapshot-item:one", "snapshot-item:two"], consumer.Commands);
    }

    [Fact]
    public void JsonQueueCodec_Operations_RoundTrip()
    {
        var codec = new JsonQueueOperationCodec<int>(Options);
        var consumer = new QueueConsumer<int>();

        Apply(codec, writer => codec.WriteEnqueue(10, writer), consumer);
        Apply(codec, writer => codec.WriteDequeue(writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot(new[] { 20, 30 }, writer), consumer);

        Assert.Equal(["enqueue:10", "dequeue", "clear", "snapshot:2", "snapshot-item:20", "snapshot-item:30"], consumer.Commands);
    }

    [Fact]
    public void JsonSetCodec_Operations_RoundTrip()
    {
        var codec = new JsonSetOperationCodec<string>(Options);
        var consumer = new SetConsumer<string>();

        Apply(codec, writer => codec.WriteAdd("a", writer), consumer);
        Apply(codec, writer => codec.WriteRemove("a", writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot(new[] { "b", "c" }, writer), consumer);

        Assert.Equal(["add:a", "remove:a", "clear", "snapshot:2", "snapshot-item:b", "snapshot-item:c"], consumer.Commands);
    }

    [Fact]
    public void JsonValueCodec_Set_RoundTrips()
    {
        var codec = new JsonValueOperationCodec<int>(Options);
        var consumer = new ValueConsumer<int>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet(42, buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(42, consumer.Value);
    }

    [Fact]
    public void JsonValueCodec_CustomValue_UsesConfiguredTypeInfo()
    {
        var codec = new JsonValueOperationCodec<JsonCodecTestValue>(CreateOptions());
        var consumer = new ValueConsumer<JsonCodecTestValue>();
        var buffer = new ArrayBufferWriter<byte>();
        var value = new JsonCodecTestValue("alpha", 3);

        codec.WriteSet(value, buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(value, consumer.Value);
    }

    [Fact]
    public void JsonValueCodec_MissingMetadata_ThrowsHelpfulException()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new JsonValueOperationCodec<JsonCodecTestValue>(new JsonSerializerOptions()));

        Assert.Contains(nameof(JsonCodecTestValue), exception.Message);
        Assert.Contains("source-generated JsonSerializerContext", exception.Message);
    }

    [Fact]
    public void JsonStateCodec_SetAndClear_RoundTrip()
    {
        var codec = new JsonStateOperationCodec<string>(Options);
        var consumer = new StateConsumer<string>();

        Apply(codec, writer => codec.WriteSet("state", 7, writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);

        Assert.Equal(["set:state:7", "clear"], consumer.Commands);
    }

    [Fact]
    public void JsonTcsCodec_States_RoundTrip()
    {
        var codec = new JsonTcsOperationCodec<int>(Options);
        var consumer = new TcsConsumer<int>();

        Apply(codec, writer => codec.WritePending(writer), consumer);
        Apply(codec, writer => codec.WriteCompleted(5, writer), consumer);
        Apply(codec, writer => codec.WriteFaulted(new InvalidOperationException("boom"), writer), consumer);
        Apply(codec, writer => codec.WriteCanceled(writer), consumer);

        Assert.Equal(["pending", "completed:5", "faulted:boom", "canceled"], consumer.Commands);
    }

    [Fact]
    public void JsonLinesLogFormat_CreateWriter_WritesReadableRecordPerLine()
    {
        var format = new JsonLinesLogFormat();
        using var writer = format.CreateWriter();

        AppendValueSet(writer, 8, 42);
        AppendValueSet(writer, 9, 43);

        Assert.Equal(
            """{"streamId":8,"entry":{"cmd":"set","value":42}}""" + "\n" +
            """{"streamId":9,"entry":{"cmd":"set","value":43}}""" + "\n",
            GetString(writer));
    }

    [Fact]
    public void JsonLinesLogFormat_Writer_DisposeWithoutCommit_TruncatesIncompleteLine()
    {
        var format = new JsonLinesLogFormat();
        using var writer = format.CreateWriter();

        AppendValueSet(writer, 8, 42);
        using (var aborted = writer.CreateLogWriter(new LogStreamId(9)).BeginEntry())
        {
            aborted.Writer.Write("""{"cmd":"set","value":100"""u8);
        }

        AppendValueSet(writer, 10, 43);

        Assert.Equal(
            """{"streamId":8,"entry":{"cmd":"set","value":42}}""" + "\n" +
            """{"streamId":10,"entry":{"cmd":"set","value":43}}""" + "\n",
            GetString(writer));
    }

    [Fact]
    public void JsonLinesLogFormat_Writer_Reset_ReusesBuffer()
    {
        var format = new JsonLinesLogFormat();
        using var writer = format.CreateWriter();

        AppendValueSet(writer, 8, 42);
        writer.Reset();
        AppendValueSet(writer, 9, 43);

        Assert.Equal("""{"streamId":9,"entry":{"cmd":"set","value":43}}""" + "\n", GetString(writer));
    }

    [Fact]
    public void JsonLinesLogFormat_DoesNotDependOnRemovedBuilderTypes()
    {
        Assert.Null(typeof(JsonLinesLogFormat).Assembly.GetType("Orleans.Journaling.Json.JsonLinesLogExtentCodec"));
        Assert.Null(typeof(ILogFormat).Assembly.GetType("Orleans.Journaling.LogExtentBuilder"));
        Assert.Null(typeof(ILogFormat).Assembly.GetType("Orleans.Journaling.IStateMachineLogExtentCodec"));
        Assert.Null(typeof(ILogFormat).Assembly.GetType("Orleans.Journaling.StateMachineStorageWriter"));
    }

    [Fact]
    public void JsonLinesLogFormat_Read_DispatchesEntries()
    {
        var format = new JsonLinesLogFormat();
        var entries = Read(format, """{"streamId":8,"entry":{"cmd":"set","value":42}}""" + "\n");
        var entry = Assert.Single(entries);
        var valueCodec = new JsonValueOperationCodec<int>(Options);
        var consumer = new ValueConsumer<int>();

        valueCodec.Apply(new ReadOnlySequence<byte>(entry.Payload), consumer);

        Assert.Equal((ulong)8, entry.StreamId.Value);
        Assert.Equal(42, consumer.Value);
    }

    [Theory]
    [InlineData("\n", "blank lines")]
    [InlineData("""[{"streamId":8,"entry":{"cmd":"set","value":42}}]""" + "\n", "must be a JSON object")]
    [InlineData("null\n", "must be a JSON object")]
    [InlineData("""{"entry":{"cmd":"set","value":42}}""" + "\n", "streamId")]
    [InlineData("""{"streamId":8}""" + "\n", "entry")]
    [InlineData("""{"streamId":"8","entry":{"cmd":"set","value":42}}""" + "\n", "unsigned integer")]
    [InlineData("""{"streamId":8,"entry":null}""" + "\n", "must be a JSON object")]
    [InlineData("""{"streamId":8,"entry":{"cmd":"set","value":42},"extra":true}""" + "\n", "unexpected property")]
    [InlineData("""{"streamId":8,"streamId":9,"entry":{"cmd":"set","value":42}}""" + "\n", "duplicate property")]
    [InlineData("""{"streamId":8,"entry":{"cmd":"set","value":42},"entry":{"cmd":"set","value":43}}""" + "\n", "duplicate property")]
    [InlineData("""{"streamId":8,"entry":{"cmd":"set","value":42}}{}""" + "\n", "invalid JSON")]
    [InlineData("""{"streamId":8,"entry":{"cmd":"set","value":42}""" + "\n", "invalid JSON")]
    public void JsonLinesLogFormat_Read_InvalidJsonLines_Throws(string jsonLines, string expectedMessage)
    {
        var format = new JsonLinesLogFormat();

        var exception = Assert.Throws<InvalidOperationException>(() => Read(format, jsonLines));

        Assert.Contains(expectedMessage, exception.Message);
    }

    [Fact]
    public void JsonLinesLogFormat_Read_Bom_Throws()
    {
        var format = new JsonLinesLogFormat();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("""{"streamId":8,"entry":{"cmd":"set","value":42}}""" + "\n")).ToArray();

        var exception = Assert.Throws<InvalidOperationException>(() => Read(format, bytes));

        Assert.Contains("byte order marks", exception.Message);
    }

    [Fact]
    public void JsonLinesLogFormat_Read_FinalLineWithoutNewline_Throws()
    {
        var format = new JsonLinesLogFormat();
        var jsonLines =
            """{"streamId":8,"entry":{"cmd":"set","value":42}}""" + "\n" +
            """{"streamId":9,"entry":{"cmd":"set","value":43}""";

        var exception = Assert.Throws<InvalidOperationException>(() => Read(format, jsonLines));

        Assert.Contains("must end with a newline", exception.Message);
    }

    [Fact]
    public void JsonLinesLogFormat_Read_PartialLineWaitsForNewlineWhenInputIsNotCompleted()
    {
        var format = new JsonLinesLogFormat();
        var bytes = Encoding.UTF8.GetBytes("""{"streamId":8,"entry":{"cmd":"set","value":42}}""");
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new ArcBufferReader(buffer);
        var consumer = new RecordingLogEntrySink();

        var result = format.TryRead(reader, consumer, isCompleted: false);

        Assert.False(result);
        Assert.Equal(bytes.Length, reader.Length);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void JsonLinesLogFormat_Read_NewlineAtArcBufferPageBoundary_Parses()
    {
        var format = new JsonLinesLogFormat();
        var prefix = "{\"streamId\":8,\"entry\":{\"text\":\"";
        var suffix = "\"}}";
        var text = new string('a', ArcBufferWriter.MinimumPageSize - Encoding.UTF8.GetByteCount(prefix + suffix));
        var line = prefix + text + suffix;
        Assert.Equal(ArcBufferWriter.MinimumPageSize, Encoding.UTF8.GetByteCount(line));
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new ArcBufferReader(buffer);
        var consumer = new RecordingLogEntrySink();

        var result = format.TryRead(reader, consumer, isCompleted: false);

        Assert.True(result);
        Assert.Equal(0, reader.Length);
        var entry = Assert.Single(consumer.Entries);
        Assert.Equal((ulong)8, entry.StreamId.Value);
        Assert.Equal($$"""{"text":"{{text}}"}""", Encoding.UTF8.GetString(entry.Payload));
    }

    [Fact]
    public void JsonLinesLogFormat_Read_MultiPagePartialLineWaitsForNewlineWhenInputIsNotCompleted()
    {
        var format = new JsonLinesLogFormat();
        var prefix = "{\"streamId\":8,\"entry\":{\"text\":\"";
        var suffix = "\"}}";
        var text = new string('a', ArcBufferWriter.MinimumPageSize + 8 - Encoding.UTF8.GetByteCount(prefix + suffix));
        var bytes = Encoding.UTF8.GetBytes(prefix + text + suffix);
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new ArcBufferReader(buffer);
        var consumer = new RecordingLogEntrySink();

        var result = format.TryRead(reader, consumer, isCompleted: false);

        Assert.False(result);
        Assert.Equal(bytes.Length, reader.Length);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void JsonLinesLogFormat_Read_ParsesCrLfLines()
    {
        var format = new JsonLinesLogFormat();
        var entries = Read(format, """{"streamId":8,"entry":{"cmd":"set","value":42}}""" + "\r\n");
        var entry = Assert.Single(entries);

        Assert.Equal((ulong)8, entry.StreamId.Value);
        Assert.Equal("""{"cmd":"set","value":42}""", Encoding.UTF8.GetString(entry.Payload));
    }

    [Fact]
    public void JsonLinesLogFormat_Read_ActiveStateMachine_UsesJsonCodecBridge()
    {
        var format = new JsonLinesLogFormat();
        var codec = new RecordingJsonLogEntryCodec();
        var stateMachine = new RecordingStateMachine(codec);

        ReadOne(format, """{"streamId":8,"entry":{"cmd":"set","value":42}}""" + "\n", new SingleStateMachineResolver(stateMachine));

        Assert.Same(stateMachine, codec.StateMachine);
        Assert.Equal(42, codec.Value);
        Assert.False(stateMachine.RawApplyCalled);
    }

    [Fact]
    public void JsonLinesLogFormat_Read_ActiveStateMachine_ThrowsWhenCodecIsNotJsonBridge()
    {
        var format = new JsonLinesLogFormat();
        var stateMachine = new RecordingStateMachine(new object());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReadOne(format, """{"streamId":8,"entry":{"cmd":"set","value":42}}""" + "\n", new SingleStateMachineResolver(stateMachine)));

        Assert.Contains("does not implement IJsonLogEntryCodec", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLinesLogFormat_Read_ActiveStateMachine_ThrowsWhenCodecHandlerDoesNotMatch()
    {
        var format = new JsonLinesLogFormat();
        var stateMachine = new RecordingStateMachine(new JsonValueOperationCodec<int>(Options));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReadOne(format, """{"streamId":8,"entry":{"cmd":"set","value":42}}""" + "\n", new SingleStateMachineResolver(stateMachine)));

        Assert.Contains("not compatible", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLinesLogFormat_Read_DurableNothingIgnoresEntryWithoutJsonCodec()
    {
        var format = new JsonLinesLogFormat();
        var stateMachine = new NoOpStateMachine();

        ReadOne(format, """{"streamId":8,"entry":{"cmd":"set","value":42}}""" + "\n", new SingleStateMachineResolver(stateMachine));

        Assert.False(stateMachine.RawApplyCalled);
    }

    [Fact]
    public void JsonLinesLogFormat_Writer_RejectsWrongFormattedEntryType()
    {
        var format = new JsonLinesLogFormat();
        using var writer = format.CreateWriter();
        var logWriter = writer.CreateLogWriter(new LogStreamId(8));

        var exception = Assert.Throws<InvalidOperationException>(() => logWriter.AppendFormattedEntry(new TestFormattedLogEntry()));

        Assert.Contains("cannot append formatted entry", exception.Message, StringComparison.Ordinal);
    }

    private static void Apply<T>(IDurableListOperationCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableListOperationHandler<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableQueueOperationCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableQueueOperationHandler<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableSetOperationCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableSetOperationHandler<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableStateOperationCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableStateOperationHandler<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableTaskCompletionSourceOperationCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static string GetString(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);

    private static string GetString(ILogSegmentWriter writer)
    {
        using var buffer = writer.GetCommittedBuffer();
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void AppendValueSet(ILogSegmentWriter writer, ulong streamId, int value)
    {
        var codec = new JsonValueOperationCodec<int>(Options);
        using var entry = writer.CreateLogWriter(new LogStreamId(streamId)).BeginEntry();
        codec.WriteSet(value, entry.Writer);
        entry.Commit();
    }

    private static JsonSerializerOptions CreateOptions() => new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    private static List<RecordedLogEntry> Read(JsonLinesLogFormat format, string jsonLines) => Read(format, Encoding.UTF8.GetBytes(jsonLines));

    private static List<RecordedLogEntry> Read(JsonLinesLogFormat format, byte[] bytes)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new ArcBufferReader(buffer);
        var consumer = new RecordingLogEntrySink();
        while (format.TryRead(reader, consumer, isCompleted: true))
        {
        }

        return consumer.Entries;
    }

    private static void ReadOne(JsonLinesLogFormat format, string jsonLines, ILogStreamStateMachineResolver resolver)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(Encoding.UTF8.GetBytes(jsonLines));
        var reader = new ArcBufferReader(buffer);
        Assert.True(format.TryRead(reader, resolver, isCompleted: true));
        Assert.Equal(0, reader.Length);
    }

    private sealed record RecordedLogEntry(LogStreamId StreamId, byte[] Payload);

    private sealed class RecordingLogEntrySink : ILogStreamStateMachineResolver, IDurableStateMachine, IFormattedLogEntryBuffer
    {
        private LogStreamId _streamId;

        public List<RecordedLogEntry> Entries { get; } = [];

        public IReadOnlyList<IFormattedLogEntry> FormattedEntries => [];

        object IDurableStateMachine.OperationCodec => this;

        public IDurableStateMachine ResolveStateMachine(LogStreamId streamId)
        {
            _streamId = streamId;
            return this;
        }

        public void AddFormattedEntry(IFormattedLogEntry entry) => Entries.Add(new(_streamId, entry.Payload.ToArray()));

        public void Apply(ReadOnlySequence<byte> payload) => Entries.Add(new(_streamId, payload.ToArray()));

        public void Reset(ILogWriter storage) { }
        public void AppendEntries(LogWriter writer) { }
        public void AppendSnapshot(LogWriter writer) { }
        public IDurableStateMachine DeepCopy() => throw new NotSupportedException();
    }

    private sealed class SingleStateMachineResolver(IDurableStateMachine stateMachine) : ILogStreamStateMachineResolver
    {
        public IDurableStateMachine ResolveStateMachine(LogStreamId streamId) => stateMachine;
    }

    private sealed class RecordingStateMachine(object codec) : IDurableStateMachine
    {
        public bool RawApplyCalled { get; private set; }

        public object OperationCodec => codec;

        public void Apply(ReadOnlySequence<byte> entry) => RawApplyCalled = true;
        public void Reset(ILogWriter storage) { }
        public void AppendEntries(LogWriter writer) { }
        public void AppendSnapshot(LogWriter writer) { }
        public IDurableStateMachine DeepCopy() => throw new NotSupportedException();
    }

    private sealed class NoOpStateMachine : IDurableNothing, IDurableStateMachine
    {
        public bool RawApplyCalled { get; private set; }

        public object OperationCodec { get; } = new();

        public void Apply(ReadOnlySequence<byte> entry) => RawApplyCalled = true;
        public void Reset(ILogWriter storage) { }
        public void AppendEntries(LogWriter writer) { }
        public void AppendSnapshot(LogWriter writer) { }
        public IDurableStateMachine DeepCopy() => throw new NotSupportedException();
    }

    private sealed class RecordingJsonLogEntryCodec : IJsonLogEntryCodec
    {
        public IDurableStateMachine? StateMachine { get; private set; }

        public int? Value { get; private set; }

        public void Apply(JsonElement entry, IDurableStateMachine stateMachine)
        {
            StateMachine = stateMachine;
            Value = entry.GetProperty(JsonLogEntryFields.Value).GetInt32();
        }
    }

    private sealed class TestFormattedLogEntry : IFormattedLogEntry
    {
        public ReadOnlyMemory<byte> Payload => ReadOnlyMemory<byte>.Empty;
    }

    private sealed class DictionaryConsumer<TKey, TValue> : IDurableDictionaryOperationHandler<TKey, TValue> where TKey : notnull
    {
        public TKey? LastSetKey { get; private set; }
        public TValue? LastSetValue { get; private set; }
        public List<KeyValuePair<TKey, TValue>> Items { get; } = [];
        public void ApplySet(TKey key, TValue value)
        {
            LastSetKey = key;
            LastSetValue = value;
        }

        public void ApplyRemove(TKey key) { }
        public void ApplyClear() => Items.Clear();
        public void ApplySnapshotStart(int count) => Items.Clear();
        public void ApplySnapshotItem(TKey key, TValue value) => Items.Add(new(key, value));
    }

    private sealed class ListConsumer<T> : IDurableListOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplySet(int index, T item) => Commands.Add($"set:{index}:{item}");
        public void ApplyInsert(int index, T item) => Commands.Add($"insert:{index}:{item}");
        public void ApplyRemoveAt(int index) => Commands.Add($"remove:{index}");
        public void ApplyClear() => Commands.Add("clear");
        public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");
        public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
    }

    private sealed class QueueConsumer<T> : IDurableQueueOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyEnqueue(T item) => Commands.Add($"enqueue:{item}");
        public void ApplyDequeue() => Commands.Add("dequeue");
        public void ApplyClear() => Commands.Add("clear");
        public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");
        public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
    }

    private sealed class SetConsumer<T> : IDurableSetOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplyRemove(T item) => Commands.Add($"remove:{item}");
        public void ApplyClear() => Commands.Add("clear");
        public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");
        public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
    }

    private sealed class ValueConsumer<T> : IDurableValueOperationHandler<T>
    {
        public T? Value { get; private set; }
        public void ApplySet(T value) => Value = value;
    }

    private sealed class StateConsumer<T> : IDurableStateOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplySet(T state, ulong version) => Commands.Add($"set:{state}:{version}");
        public void ApplyClear() => Commands.Add("clear");
    }

    private sealed class TcsConsumer<T> : IDurableTaskCompletionSourceOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyPending() => Commands.Add("pending");
        public void ApplyCompleted(T value) => Commands.Add($"completed:{value}");
        public void ApplyFaulted(Exception exception) => Commands.Add($"faulted:{exception.Message}");
        public void ApplyCanceled() => Commands.Add("canceled");
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
