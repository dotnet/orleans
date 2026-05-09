using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Json;
using Orleans.Journaling.Tests;
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

        var input = AssertPayload("""["set","alice",42]""", writer => codec.WriteSet("alice", 42, writer));
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(input, consumer);

        Assert.Equal("alice", consumer.LastSetKey);
        Assert.Equal(42, consumer.LastSetValue);
    }

    [Fact]
    public void UseJsonJournalFormat_TypeInfoResolverOverload_RegistersPayloadMetadata()
    {
        var builder = new TestSiloBuilder();
        builder.UseJsonJournalFormat(JsonCodecTestJsonContext.Default);
        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.IsType<JsonLinesJournalFormat>(serviceProvider.GetRequiredKeyedService<IJournalFormat>(JsonJournalExtensions.JournalFormatKey));
        var codec = serviceProvider.GetRequiredKeyedService<IDurableValueOperationCodecProvider>(JsonJournalExtensions.JournalFormatKey).GetCodec<JsonCodecTestValue>();

        var input = CodecTestHelpers.WriteEntry(writer => codec.WriteSet(new("test", 1), writer));
        var consumer = new ValueConsumer<JsonCodecTestValue>();
        codec.Apply(input, consumer);

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

        var input = AssertPayload("""["snapshot",[["alpha",1],["beta",2]]]""", writer => codec.WriteSnapshot(items, writer));
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(input, consumer);

        Assert.Equal(items, consumer.Items);
    }

    [Fact]
    public void JsonDictionaryCodec_RemoveAndClear_WriteExpectedPayloads()
    {
        var codec = new JsonDictionaryOperationCodec<string, int>(Options);

        AssertPayload("""["remove","alice"]""", writer => codec.WriteRemove("alice", writer));
        AssertPayload("""["clear"]""", writer => codec.WriteClear(writer));
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

        codec.Apply(CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(new[] { "one", "two" }, writer)), consumer);

        Assert.Equal(["reset:2", "add:one", "add:two"], consumer.Commands);
    }

    [Fact]
    public void JsonListCodec_Snapshot_ThrowsWhenCollectionCountDoesNotMatchEnumeration()
    {
        var codec = new JsonListOperationCodec<string>(Options);

        var tooManyItems = new MiscountedReadOnlyCollection<string>(1, ["one", "two"]);
        var tooFewItems = new MiscountedReadOnlyCollection<string>(2, ["one"]);

        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooManyItems, writer)));
        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooFewItems, writer)));
    }

    [Fact]
    public void JsonQueueCodec_Snapshot_ThrowsWhenCollectionCountDoesNotMatchEnumeration()
    {
        var codec = new JsonQueueOperationCodec<string>(Options);

        var tooManyItems = new MiscountedReadOnlyCollection<string>(1, ["one", "two"]);
        var tooFewItems = new MiscountedReadOnlyCollection<string>(2, ["one"]);

        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooManyItems, writer)));
        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooFewItems, writer)));
    }

    [Fact]
    public void JsonSetCodec_Snapshot_ThrowsWhenCollectionCountDoesNotMatchEnumeration()
    {
        var codec = new JsonSetOperationCodec<string>(Options);

        var tooManyItems = new MiscountedReadOnlyCollection<string>(1, ["one", "two"]);
        var tooFewItems = new MiscountedReadOnlyCollection<string>(2, ["one"]);

        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooManyItems, writer)));
        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooFewItems, writer)));
    }

    [Fact]
    public void JsonDictionaryCodec_Snapshot_ThrowsWhenCollectionCountDoesNotMatchEnumeration()
    {
        var codec = new JsonDictionaryOperationCodec<string, int>(Options);

        var tooManyItems = new MiscountedReadOnlyCollection<KeyValuePair<string, int>>(
            1,
            [new("one", 1), new("two", 2)]);
        var tooFewItems = new MiscountedReadOnlyCollection<KeyValuePair<string, int>>(
            2,
            [new("one", 1)]);

        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooManyItems, writer)));
        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooFewItems, writer)));
    }

    [Fact]
    public void JsonQueueCodec_Operations_RoundTrip()
    {
        var codec = new JsonQueueOperationCodec<int>(Options);
        var consumer = new QueueConsumer<int>();

        codec.Apply(AssertPayload("""["enqueue",10]""", writer => codec.WriteEnqueue(10, writer)), consumer);
        codec.Apply(AssertPayload("""["dequeue"]""", writer => codec.WriteDequeue(writer)), consumer);
        codec.Apply(AssertPayload("""["clear"]""", writer => codec.WriteClear(writer)), consumer);
        codec.Apply(AssertPayload("""["snapshot",[20,30]]""", writer => codec.WriteSnapshot(new[] { 20, 30 }, writer)), consumer);

        Assert.Equal(["enqueue:10", "dequeue", "clear", "reset:2", "enqueue:20", "enqueue:30"], consumer.Commands);
    }

    [Fact]
    public void JsonSetCodec_Operations_RoundTrip()
    {
        var codec = new JsonSetOperationCodec<string>(Options);
        var consumer = new SetConsumer<string>();

        codec.Apply(AssertPayload("""["add","a"]""", writer => codec.WriteAdd("a", writer)), consumer);
        codec.Apply(AssertPayload("""["remove","a"]""", writer => codec.WriteRemove("a", writer)), consumer);
        codec.Apply(AssertPayload("""["clear"]""", writer => codec.WriteClear(writer)), consumer);
        codec.Apply(AssertPayload("""["snapshot",["b","c"]]""", writer => codec.WriteSnapshot(new[] { "b", "c" }, writer)), consumer);

        Assert.Equal(["add:a", "remove:a", "clear", "reset:2", "add:b", "add:c"], consumer.Commands);
    }

    [Fact]
    public void JsonValueCodec_Set_RoundTrips()
    {
        var codec = new JsonValueOperationCodec<int>(Options);
        var consumer = new ValueConsumer<int>();

        codec.Apply(AssertPayload("""["set",42]""", writer => codec.WriteSet(42, writer)), consumer);

        Assert.Equal(42, consumer.Value);
    }

    [Fact]
    public void JsonValueCodec_CustomValue_UsesConfiguredTypeInfo()
    {
        var codec = new JsonValueOperationCodec<JsonCodecTestValue>(CreateOptions());
        var consumer = new ValueConsumer<JsonCodecTestValue>();
        var value = new JsonCodecTestValue("alpha", 3);

        codec.Apply(CodecTestHelpers.WriteEntry(writer => codec.WriteSet(value, writer)), consumer);

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

        codec.Apply(AssertPayload("""["set","state",7]""", writer => codec.WriteSet("state", 7, writer)), consumer);
        codec.Apply(AssertPayload("""["clear"]""", writer => codec.WriteClear(writer)), consumer);

        Assert.Equal(["set:state:7", "clear"], consumer.Commands);
    }

    [Fact]
    public void JsonTcsCodec_States_RoundTrip()
    {
        var codec = new JsonTcsOperationCodec<int>(Options);
        var consumer = new TcsConsumer<int>();

        codec.Apply(AssertPayload("""["pending"]""", writer => codec.WritePending(writer)), consumer);
        codec.Apply(AssertPayload("""["completed",5]""", writer => codec.WriteCompleted(5, writer)), consumer);
        codec.Apply(AssertPayload("""["faulted","boom"]""", writer => codec.WriteFaulted(new InvalidOperationException("boom"), writer)), consumer);
        codec.Apply(AssertPayload("""["canceled"]""", writer => codec.WriteCanceled(writer)), consumer);

        Assert.Equal(["pending", "completed:5", "faulted:boom", "canceled"], consumer.Commands);
    }

    [Fact]
    public void JsonLinesJournalFormat_CreateWriter_WritesReadableRecordPerLine()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();

        AppendValueSet(writer, 8, 42);
        AppendValueSet(writer, 9, 43);

        Assert.Equal(
            """[8,"set",42]""" + "\n" +
            """[9,"set",43]""" + "\n",
            GetString(writer));
    }

    [Fact]
    public void JsonLinesJournalFormat_TryAppendFormattedEntry_WritesEntryDirectly()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();

        var accepted = writer.CreateJournalStreamWriter(new JournalStreamId(8)).TryAppendFormattedEntry(JsonFormattedJournalEntry.Create(42, static (jsonWriter, value) =>
        {
            jsonWriter.WriteStringValue(JsonJournalEntryCommands.Set);
            jsonWriter.WriteNumberValue(value);
        }));

        Assert.True(accepted);
        Assert.Equal("""[8,"set",42]""" + "\n", GetString(writer));
    }

    [Fact]
    public void JsonValueCodec_JournalStreamWriterOverload_UsesFormattedEntryForJsonWriter()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        var codec = new JsonValueOperationCodec<int>(Options);

        codec.WriteSet(42, writer.CreateJournalStreamWriter(new JournalStreamId(8)));

        Assert.Equal("""[8,"set",42]""" + "\n", GetString(writer));
    }

    [Fact]
    public void JsonListCodec_JournalStreamWriterOverload_WritesAddDirectlyForJsonWriter()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        var codec = new JsonListOperationCodec<string>(Options);

        codec.WriteAdd("one", writer.CreateJournalStreamWriter(new JournalStreamId(8)));

        Assert.Equal("""[8,"add","one"]""" + "\n", GetString(writer));
    }

    [Fact]
    public void JsonOperationCodecs_JournalStreamWriterOverload_WriteDirectJsonLinesForOperationFamilies()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();

        new JsonQueueOperationCodec<int>(Options).WriteEnqueue(10, writer.CreateJournalStreamWriter(new JournalStreamId(11)));
        new JsonSetOperationCodec<string>(Options).WriteAdd("a", writer.CreateJournalStreamWriter(new JournalStreamId(12)));
        new JsonDictionaryOperationCodec<string, int>(Options).WriteSet("alice", 42, writer.CreateJournalStreamWriter(new JournalStreamId(13)));
        new JsonTcsOperationCodec<int>(Options).WriteCompleted(5, writer.CreateJournalStreamWriter(new JournalStreamId(14)));

        Assert.Equal(
            """[11,"enqueue",10]""" + "\n" +
            """[12,"add","a"]""" + "\n" +
            """[13,"set","alice",42]""" + "\n" +
            """[14,"completed",5]""" + "\n",
            GetString(writer));
    }

    [Fact]
    public void JsonLinesJournalFormat_TryAppendFormattedEntry_RollsBackWhenJsonWriteThrows()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        var codec = new JsonListOperationCodec<ThrowingJsonValue>(Options);

        AppendValueSet(writer, 8, 42);
        Assert.Throws<InvalidOperationException>(() => codec.WriteAdd(new("bad"), writer.CreateJournalStreamWriter(new JournalStreamId(9))));
        AppendValueSet(writer, 10, 43);

        Assert.Equal(
            """[8,"set",42]""" + "\n" +
            """[10,"set",43]""" + "\n",
            GetString(writer));
    }

    [Fact]
    public void JsonValueCodec_JournalStreamWriterOverload_FallsBackToPayloadBytesForNonJsonWriter()
    {
        using var writer = new CapturingNonJsonJournalWriter();
        var codec = new JsonValueOperationCodec<int>(Options);

        codec.WriteSet(42, writer.CreateJournalStreamWriter(new JournalStreamId(8)));

        var payload = """["set",42]"""u8.ToArray();
        var entry = Assert.Single(writer.Entries);

        Assert.Equal((ulong)8, entry.StreamId.Value);
        Assert.Equal(payload, entry.Payload);
    }

    [Fact]
    public void JsonOperationCodecWriter_FallbackPath_AbortsEntryWhenJsonWriteThrows()
    {
        using var writer = new CapturingNonJsonJournalWriter();
        var valueCodec = new JsonValueOperationCodec<int>(Options);
        var throwingCodec = new JsonValueOperationCodec<ThrowingJsonValue>(Options);

        valueCodec.WriteSet(42, writer.CreateJournalStreamWriter(new JournalStreamId(8)));
        Assert.Throws<InvalidOperationException>(() => throwingCodec.WriteSet(new("bad"), writer.CreateJournalStreamWriter(new JournalStreamId(9))));
        valueCodec.WriteSet(43, writer.CreateJournalStreamWriter(new JournalStreamId(10)));

        Assert.Collection(
            writer.Entries,
            entry =>
            {
                Assert.Equal((ulong)8, entry.StreamId.Value);
                Assert.Equal("""["set",42]""", Encoding.UTF8.GetString(entry.Payload));
            },
            entry =>
            {
                Assert.Equal((ulong)10, entry.StreamId.Value);
                Assert.Equal("""["set",43]""", Encoding.UTF8.GetString(entry.Payload));
            });
    }

    [Fact]
    public void JsonLinesJournalFormat_Writer_DisposeWithoutCommit_TruncatesIncompleteLine()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();

        AppendValueSet(writer, 8, 42);
        using (var aborted = writer.CreateJournalStreamWriter(new JournalStreamId(9)).BeginEntry())
        {
            aborted.Writer.Write("""["set",100"""u8);
        }

        AppendValueSet(writer, 10, 43);

        Assert.Equal(
            """[8,"set",42]""" + "\n" +
            """[10,"set",43]""" + "\n",
            GetString(writer));
    }

    [Fact]
    public void JsonLinesJournalFormat_Writer_Reset_ReusesBuffer()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();

        AppendValueSet(writer, 8, 42);
        writer.Reset();
        AppendValueSet(writer, 9, 43);

        Assert.Equal("""[9,"set",43]""" + "\n", GetString(writer));
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_DispatchesEntries()
    {
        var format = new JsonLinesJournalFormat();
        var entries = Read(format, """[8,"set",42]""" + "\n");
        var entry = Assert.Single(entries);
        var valueCodec = new JsonValueOperationCodec<int>(Options);
        var consumer = new ValueConsumer<int>();

        valueCodec.Apply(new ReadOnlySequence<byte>(entry.Payload), consumer);

        Assert.Equal((ulong)8, entry.StreamId.Value);
        Assert.Equal(42, consumer.Value);
    }

    [Theory]
    [InlineData("\n", "blank lines")]
    [InlineData("""{"streamId":8,"entry":["set",42]}""" + "\n", "must be a JSON array")]
    [InlineData("null\n", "must be a JSON array")]
    [InlineData("[]\n", "stream id")]
    [InlineData("[8]\n", "operation command")]
    [InlineData("""["8","set",42]""" + "\n", "unsigned integer")]
    [InlineData("[8,null]\n", "operation command string")]
    [InlineData("""[8,"set",42]{}""" + "\n", "invalid JSON")]
    [InlineData("""[8,"set",42""" + "\n", "invalid JSON")]
    public void JsonLinesJournalFormat_Read_InvalidJsonLines_Throws(string jsonLines, string expectedMessage)
    {
        var format = new JsonLinesJournalFormat();

        var exception = Assert.Throws<InvalidOperationException>(() => Read(format, jsonLines));

        Assert.Contains(expectedMessage, exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_Bom_Throws()
    {
        var format = new JsonLinesJournalFormat();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("""[8,"set",42]""" + "\n")).ToArray();

        var exception = Assert.Throws<InvalidOperationException>(() => Read(format, bytes));

        Assert.Contains("byte order marks", exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_FinalLineWithoutNewline_Throws()
    {
        var format = new JsonLinesJournalFormat();
        var jsonLines =
            """[8,"set",42]""" + "\n" +
            """[9,"set",43""";

        var exception = Assert.Throws<InvalidOperationException>(() => Read(format, jsonLines));

        Assert.Contains("must end with a newline", exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_PartialLineWaitsForNewlineWhenInputIsNotCompleted()
    {
        var format = new JsonLinesJournalFormat();
        var bytes = Encoding.UTF8.GetBytes("""[8,"set",42]""");
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new JournalReadBuffer(new ArcBufferReader(buffer), isCompleted: false);
        var consumer = new RecordingJournalEntrySink();

        format.Read(reader, consumer);

        Assert.Equal(bytes.Length, reader.Length);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_NewlineAtArcBufferPageBoundary_Parses()
    {
        var format = new JsonLinesJournalFormat();
        var prefix = "[8,\"set\",\"";
        var suffix = "\"]";
        var text = new string('a', ArcBufferWriter.MinimumPageSize - Encoding.UTF8.GetByteCount(prefix + suffix));
        var line = prefix + text + suffix;
        Assert.Equal(ArcBufferWriter.MinimumPageSize, Encoding.UTF8.GetByteCount(line));
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new JournalReadBuffer(new ArcBufferReader(buffer), isCompleted: false);
        var consumer = new RecordingJournalEntrySink();

        format.Read(reader, consumer);

        Assert.Equal(0, reader.Length);
        var entry = Assert.Single(consumer.Entries);
        Assert.Equal((ulong)8, entry.StreamId.Value);
        Assert.Equal($$"""["set","{{text}}"]""", Encoding.UTF8.GetString(entry.Payload));
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_MultiPagePartialLineWaitsForNewlineWhenInputIsNotCompleted()
    {
        var format = new JsonLinesJournalFormat();
        var prefix = "[8,\"set\",\"";
        var suffix = "\"]";
        var text = new string('a', ArcBufferWriter.MinimumPageSize + 8 - Encoding.UTF8.GetByteCount(prefix + suffix));
        var bytes = Encoding.UTF8.GetBytes(prefix + text + suffix);
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new JournalReadBuffer(new ArcBufferReader(buffer), isCompleted: false);
        var consumer = new RecordingJournalEntrySink();

        format.Read(reader, consumer);

        Assert.Equal(bytes.Length, reader.Length);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_ParsesCrLfLines()
    {
        var format = new JsonLinesJournalFormat();
        var entries = Read(format, """[8,"set",42]""" + "\r\n");
        var entry = Assert.Single(entries);

        Assert.Equal((ulong)8, entry.StreamId.Value);
        Assert.Equal("""["set",42]""", Encoding.UTF8.GetString(entry.Payload));
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_ActiveState_UsesJsonCodecBridge()
    {
        var format = new JsonLinesJournalFormat();
        var codec = new RecordingJsonJournalEntryCodec();
        var state = new RecordingState(codec);

        ReadOne(format, """[8,"set",42]""" + "\n", new SingleStateResolver(state));

        Assert.Same(state, codec.State);
        Assert.Equal(42, codec.Value);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_ActiveState_ThrowsWhenCodecIsNotJsonBridge()
    {
        var format = new JsonLinesJournalFormat();
        var state = new RecordingState(new object());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReadOne(format, """[8,"set",42]""" + "\n", new SingleStateResolver(state)));

        Assert.Contains("does not implement IJsonJournalEntryCodec", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_ActiveState_ThrowsWhenCodecHandlerDoesNotMatch()
    {
        var format = new JsonLinesJournalFormat();
        var state = new RecordingState(new JsonValueOperationCodec<int>(Options));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReadOne(format, """[8,"set",42]""" + "\n", new SingleStateResolver(state)));

        Assert.Contains("not compatible", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_DurableNothingIgnoresEntryWithoutJsonCodec()
    {
        var format = new JsonLinesJournalFormat();
        var state = new NoOpState();

        ReadOne(format, """[8,"set",42]""" + "\n", new SingleStateResolver(state));
    }

    [Fact]
    public void JsonFormattedJournalEntry_Apply_UsesOperationCodec()
    {
        var entry = JsonFormattedJournalEntry.Create(
            42,
            static (writer, value) =>
            {
                writer.WriteStringValue(JsonJournalEntryCommands.Set);
                writer.WriteNumberValue(value);
            });
        var codec = new RecordingJsonJournalEntryCodec();
        var state = new RecordingState(codec);

        entry.Apply(state);

        Assert.Same(state, codec.State);
        Assert.Equal(42, codec.Value);
    }

    [Fact]
    public void JsonFormattedJournalEntry_Apply_DurableNothingIgnoresEntryWithoutJsonCodec()
    {
        var entry = JsonFormattedJournalEntry.Create(
            42,
            static (writer, value) =>
            {
                writer.WriteStringValue(JsonJournalEntryCommands.Set);
                writer.WriteNumberValue(value);
            });

        entry.Apply(new NoOpState());
    }

    [Fact]
    public void JsonLinesJournalFormat_Writer_RejectsWrongFormattedEntryType()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        var journalWriter = writer.CreateJournalStreamWriter(new JournalStreamId(8));

        var exception = Assert.Throws<InvalidOperationException>(() => journalWriter.AppendFormattedEntry(new TestFormattedJournalEntry()));

        Assert.Contains("cannot append formatted entry", exception.Message, StringComparison.Ordinal);
    }

    private static void Apply<T>(IDurableListOperationCodec<T> codec, Action<JournalStreamWriter> write, IDurableListOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(IDurableQueueOperationCodec<T> codec, Action<JournalStreamWriter> write, IDurableQueueOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(IDurableSetOperationCodec<T> codec, Action<JournalStreamWriter> write, IDurableSetOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(IDurableStateOperationCodec<T> codec, Action<JournalStreamWriter> write, IDurableStateOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(IDurableTaskCompletionSourceOperationCodec<T> codec, Action<JournalStreamWriter> write, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static ReadOnlySequence<byte> AssertPayload(string expectedJson, Action<JournalStreamWriter> write)
    {
        var payload = CodecTestHelpers.WriteEntry(write);
        Assert.Equal(expectedJson, GetString(payload));
        return payload;
    }

    private static string GetString(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);

    private static string GetString(ReadOnlySequence<byte> payload) => Encoding.UTF8.GetString(payload.ToArray());

    private static string GetString(IJournalBatchWriter writer)
    {
        using var buffer = writer.GetCommittedBuffer();
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void AppendValueSet(IJournalBatchWriter writer, ulong streamId, int value)
    {
        var codec = new JsonValueOperationCodec<int>(Options);
        codec.WriteSet(value, writer.CreateJournalStreamWriter(new JournalStreamId(streamId)));
    }

    private static JsonSerializerOptions CreateOptions() => new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    private static List<RecordedJournalEntry> Read(JsonLinesJournalFormat format, string jsonLines) => Read(format, Encoding.UTF8.GetBytes(jsonLines));

    private static List<RecordedJournalEntry> Read(JsonLinesJournalFormat format, byte[] bytes)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new JournalReadBuffer(new ArcBufferReader(buffer), isCompleted: true);
        var consumer = new RecordingJournalEntrySink();
        format.Read(reader, consumer);
        Assert.Equal(0, reader.Length);

        return consumer.Entries;
    }

    private static void ReadOne(JsonLinesJournalFormat format, string jsonLines, IStateResolver resolver)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(Encoding.UTF8.GetBytes(jsonLines));
        var reader = new JournalReadBuffer(new ArcBufferReader(buffer), isCompleted: true);
        format.Read(reader, resolver);
        Assert.Equal(0, reader.Length);
    }

    private sealed record RecordedJournalEntry(JournalStreamId StreamId, byte[] Payload);

    private sealed class CapturingNonJsonJournalWriter : IJournalStreamWriterTarget, IJournalEntryWriterTarget, IDisposable
    {
        private readonly ArcBufferWriter _payload = new();
        private readonly JournalEntryWriter _entryWriter = new();
        private JournalStreamId _streamId;

        public List<RecordedJournalEntry> Entries { get; } = [];

        public JournalStreamWriter CreateJournalStreamWriter(JournalStreamId streamId) => new(streamId, this);

        public void Advance(int count) => _payload.AdvanceWriter(count);

        public Memory<byte> GetMemory(int sizeHint = 0) => _payload.GetMemory(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => _payload.GetSpan(sizeHint);

        public void Write(ReadOnlySpan<byte> value) => _payload.Write(value);

        public void Write(ReadOnlySequence<byte> value) => _payload.Write(value);

        public void CommitEntry(int entryStart)
        {
            using var payload = _payload.PeekSlice(_payload.Length);
            Entries.Add(new(_streamId, payload.ToArray()));
            _payload.Reset();
        }

        public void AbortEntry(int entryStart) => _payload.Reset();

        public void Dispose() => _payload.Dispose();

        JournalEntryWriter IJournalStreamWriterTarget.BeginEntry(JournalStreamId streamId, IJournalEntryWriterCompletion? completion)
        {
            if (_entryWriter.IsActive)
            {
                throw new InvalidOperationException("The test writer already has an active entry.");
            }

            _streamId = streamId;
            _payload.Reset();
            _entryWriter.Initialize(this, Entries.Count, completion);
            return _entryWriter;
        }

        void IJournalStreamWriterTarget.AppendFormattedEntry(JournalStreamId streamId, IFormattedJournalEntry entry) =>
            throw new InvalidOperationException("This test writer does not accept formatted entries.");

        bool IJournalStreamWriterTarget.TryAppendFormattedEntry(JournalStreamId streamId, IFormattedJournalEntry entry) => false;
    }

    private sealed class RecordingJournalEntrySink : IStateResolver, IJournaledState, IFormattedJournalEntryBuffer
    {
        private JournalStreamId _streamId;

        public List<RecordedJournalEntry> Entries { get; } = [];

        public IReadOnlyList<IFormattedJournalEntry> FormattedEntries => [];

        object IJournaledState.OperationCodec => this;

        public IJournaledState ResolveState(JournalStreamId streamId)
        {
            _streamId = streamId;
            return this;
        }

        public void AddFormattedEntry(IFormattedJournalEntry entry) => Entries.Add(new(_streamId, entry.Payload.ToArray()));

        public void Reset(JournalStreamWriter storage) { }
        public void AppendEntries(JournalStreamWriter writer) { }
        public void AppendSnapshot(JournalStreamWriter writer) { }
        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class SingleStateResolver(IJournaledState state) : IStateResolver
    {
        public IJournaledState ResolveState(JournalStreamId streamId) => state;
    }

    private sealed class RecordingState(object codec) : IJournaledState
    {
        public object OperationCodec => codec;

        public void Reset(JournalStreamWriter storage) { }
        public void AppendEntries(JournalStreamWriter writer) { }
        public void AppendSnapshot(JournalStreamWriter writer) { }
        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class NoOpState : IDurableNothing, IJournaledState
    {
        public object OperationCodec { get; } = new();

        public void Reset(JournalStreamWriter storage) { }
        public void AppendEntries(JournalStreamWriter writer) { }
        public void AppendSnapshot(JournalStreamWriter writer) { }
        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class RecordingJsonJournalEntryCodec : IJsonJournalEntryCodec
    {
        public IJournaledState? State { get; private set; }

        public int? Value { get; private set; }

        public void Apply(ref JsonOperationReader reader, IJournaledState state)
        {
            State = state;
            Value = reader.Deserialize(1, JsonJournalEntryFields.Value, JsonCodecTestJsonContext.Default.Int32);
            reader.EnsureEnd(2);
        }
    }

    private sealed class TestFormattedJournalEntry : IFormattedJournalEntry
    {
        public ReadOnlyMemory<byte> Payload => ReadOnlyMemory<byte>.Empty;

        public void Apply(IJournaledState state) => throw new NotSupportedException();
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
            Items.Add(new(key, value));
        }

        public void ApplyRemove(TKey key) { }
        public void ApplyClear() => Items.Clear();
        public void Reset(int capacityHint) => Items.Clear();
    }

    private sealed class ListConsumer<T> : IDurableListOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplySet(int index, T item) => Commands.Add($"set:{index}:{item}");
        public void ApplyInsert(int index, T item) => Commands.Add($"insert:{index}:{item}");
        public void ApplyRemoveAt(int index) => Commands.Add($"remove:{index}");
        public void ApplyClear() => Commands.Add("clear");
        public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
    }

    private sealed class QueueConsumer<T> : IDurableQueueOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyEnqueue(T item) => Commands.Add($"enqueue:{item}");
        public void ApplyDequeue() => Commands.Add("dequeue");
        public void ApplyClear() => Commands.Add("clear");
        public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
    }

    private sealed class SetConsumer<T> : IDurableSetOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplyRemove(T item) => Commands.Add($"remove:{item}");
        public void ApplyClear() => Commands.Add("clear");
        public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
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
