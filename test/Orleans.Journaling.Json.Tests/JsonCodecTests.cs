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
        var codec = new JsonDurableDictionaryCommandCodec<string, int>(Options);

        var input = AssertPayload("""["set","alice",42]""", writer => codec.WriteSet("alice", 42, writer));
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(CodecTestHelpers.ReadBuffer(input), consumer);

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
        var codec = serviceProvider.GetRequiredKeyedService<IDurableValueCommandCodec<JsonCodecTestValue>>(JsonJournalExtensions.JournalFormatKey);

        var input = CodecTestHelpers.WriteEntry(writer => codec.WriteSet(new("test", 1), writer));
        var consumer = new ValueConsumer<JsonCodecTestValue>();
        codec.Apply(CodecTestHelpers.ReadBuffer(input), consumer);

        Assert.Equal(new("test", 1), consumer.Value);
    }

    [Fact]
    public void UseJsonJournalFormat_AfterAddJournalStorage_ReplacesDefaultPayloadMetadata()
    {
        var builder = new TestSiloBuilder();
        builder.AddJournalStorage();
        builder.UseJsonJournalFormat(JsonCodecTestJsonContext.Default);
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var codec = serviceProvider.GetRequiredKeyedService<IDurableValueCommandCodec<JsonCodecTestValue>>(JsonJournalExtensions.JournalFormatKey);

        var input = CodecTestHelpers.WriteEntry(writer => codec.WriteSet(new("test", 1), writer));
        var consumer = new ValueConsumer<JsonCodecTestValue>();
        codec.Apply(CodecTestHelpers.ReadBuffer(input), consumer);

        Assert.Equal(new("test", 1), consumer.Value);
    }

    [Fact]
    public void JsonDictionaryCodec_Snapshot_RoundTrips()
    {
        var codec = new JsonDurableDictionaryCommandCodec<string, int>(Options);
        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
        };

        var input = AssertPayload("""["snapshot",[["alpha",1],["beta",2]]]""", writer => codec.WriteSnapshot(items, writer));
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(CodecTestHelpers.ReadBuffer(input), consumer);

        Assert.Equal(items, consumer.Items);
    }

    [Fact]
    public void JsonDictionaryCodec_RemoveAndClear_WriteExpectedPayloads()
    {
        var codec = new JsonDurableDictionaryCommandCodec<string, int>(Options);

        AssertPayload("""["remove","alice"]""", writer => codec.WriteRemove("alice", writer));
        AssertPayload("""["clear"]""", writer => codec.WriteClear(writer));
    }

    [Fact]
    public void JsonListCodec_Operations_RoundTrip()
    {
        var codec = new JsonDurableListCommandCodec<string>(Options);
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
        var codec = new JsonDurableListCommandCodec<string>(Options);
        var consumer = new ListConsumer<string>();

        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(new[] { "one", "two" }, writer))), consumer);

        Assert.Equal(["reset:2", "add:one", "add:two"], consumer.Commands);
    }

    [Fact]
    public void JsonListCodec_Snapshot_ThrowsWhenCollectionCountDoesNotMatchEnumeration()
    {
        var codec = new JsonDurableListCommandCodec<string>(Options);

        var tooManyItems = new MiscountedReadOnlyCollection<string>(1, ["one", "two"]);
        var tooFewItems = new MiscountedReadOnlyCollection<string>(2, ["one"]);

        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooManyItems, writer)));
        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooFewItems, writer)));
    }

    [Fact]
    public void JsonQueueCodec_Snapshot_ThrowsWhenCollectionCountDoesNotMatchEnumeration()
    {
        var codec = new JsonDurableQueueCommandCodec<string>(Options);

        var tooManyItems = new MiscountedReadOnlyCollection<string>(1, ["one", "two"]);
        var tooFewItems = new MiscountedReadOnlyCollection<string>(2, ["one"]);

        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooManyItems, writer)));
        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooFewItems, writer)));
    }

    [Fact]
    public void JsonSetCodec_Snapshot_ThrowsWhenCollectionCountDoesNotMatchEnumeration()
    {
        var codec = new JsonDurableSetCommandCodec<string>(Options);

        var tooManyItems = new MiscountedReadOnlyCollection<string>(1, ["one", "two"]);
        var tooFewItems = new MiscountedReadOnlyCollection<string>(2, ["one"]);

        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooManyItems, writer)));
        Assert.Throws<InvalidOperationException>(() => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(tooFewItems, writer)));
    }

    [Fact]
    public void JsonDictionaryCodec_Snapshot_ThrowsWhenCollectionCountDoesNotMatchEnumeration()
    {
        var codec = new JsonDurableDictionaryCommandCodec<string, int>(Options);

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
        var codec = new JsonDurableQueueCommandCodec<int>(Options);
        var consumer = new QueueConsumer<int>();

        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["enqueue",10]""", writer => codec.WriteEnqueue(10, writer))), consumer);
        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["dequeue"]""", writer => codec.WriteDequeue(writer))), consumer);
        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["clear"]""", writer => codec.WriteClear(writer))), consumer);
        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["snapshot",[20,30]]""", writer => codec.WriteSnapshot(new[] { 20, 30 }, writer))), consumer);

        Assert.Equal(["enqueue:10", "dequeue", "clear", "reset:2", "enqueue:20", "enqueue:30"], consumer.Commands);
    }

    [Fact]
    public void JsonSetCodec_Operations_RoundTrip()
    {
        var codec = new JsonDurableSetCommandCodec<string>(Options);
        var consumer = new SetConsumer<string>();

        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["add","a"]""", writer => codec.WriteAdd("a", writer))), consumer);
        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["remove","a"]""", writer => codec.WriteRemove("a", writer))), consumer);
        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["clear"]""", writer => codec.WriteClear(writer))), consumer);
        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["snapshot",["b","c"]]""", writer => codec.WriteSnapshot(new[] { "b", "c" }, writer))), consumer);

        Assert.Equal(["add:a", "remove:a", "clear", "reset:2", "add:b", "add:c"], consumer.Commands);
    }

    [Fact]
    public void JsonValueCodec_Set_RoundTrips()
    {
        var codec = new JsonDurableValueCommandCodec<int>(Options);
        var consumer = new ValueConsumer<int>();

        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["set",42]""", writer => codec.WriteSet(42, writer))), consumer);

        Assert.Equal(42, consumer.Value);
    }

    [Fact]
    public void JsonValueCodec_CustomValue_UsesConfiguredTypeInfo()
    {
        var codec = new JsonDurableValueCommandCodec<JsonCodecTestValue>(CreateOptions());
        var consumer = new ValueConsumer<JsonCodecTestValue>();
        var value = new JsonCodecTestValue("alpha", 3);

        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(writer => codec.WriteSet(value, writer))), consumer);

        Assert.Equal(value, consumer.Value);
    }

    [Fact]
    public void JsonValueCodec_MissingMetadata_ThrowsHelpfulException()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new JsonDurableValueCommandCodec<JsonCodecTestValue>(new JsonSerializerOptions()));

        Assert.Contains(nameof(JsonCodecTestValue), exception.Message);
        Assert.Contains("source-generated JsonSerializerContext", exception.Message);
    }

    [Fact]
    public void JsonStateCodec_SetAndClear_RoundTrip()
    {
        var codec = new JsonPersistentStateCommandCodec<string>(Options);
        var consumer = new StateConsumer<string>();

        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["set","state",7]""", writer => codec.WriteSet("state", 7, writer))), consumer);
        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["clear"]""", writer => codec.WriteClear(writer))), consumer);

        Assert.Equal(["set:state:7", "clear"], consumer.Commands);
    }

    [Fact]
    public void JsonTcsCodec_States_RoundTrip()
    {
        var codec = new JsonDurableTaskCompletionSourceCommandCodec<int>(Options);
        var consumer = new TcsConsumer<int>();

        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["pending"]""", writer => codec.WritePending(writer))), consumer);
        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["completed",5]""", writer => codec.WriteCompleted(5, writer))), consumer);
        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["faulted","boom"]""", writer => codec.WriteFaulted(new InvalidOperationException("boom"), writer))), consumer);
        codec.Apply(CodecTestHelpers.ReadBuffer(AssertPayload("""["canceled"]""", writer => codec.WriteCanceled(writer))), consumer);

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
            """[8,["set",42]]""" + "\n" +
            """[9,["set",43]]""" + "\n",
            GetString(writer));
    }

    [Fact]
    public void JsonValueCodec_JournalStreamWriterOverload_WritesJsonLines()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        var codec = new JsonDurableValueCommandCodec<int>(Options);

        codec.WriteSet(42, writer.CreateJournalStreamWriter(new JournalStreamId(8)));

        Assert.Equal("""[8,["set",42]]""" + "\n", GetString(writer));
    }

    [Fact]
    public void JsonListCodec_JournalStreamWriterOverload_WritesAddForJsonWriter()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        var codec = new JsonDurableListCommandCodec<string>(Options);

        codec.WriteAdd("one", writer.CreateJournalStreamWriter(new JournalStreamId(8)));

        Assert.Equal("""[8,["add","one"]]""" + "\n", GetString(writer));
    }

    [Fact]
    public void JsonCommandCodecs_JournalStreamWriterOverload_WriteJsonLinesForCommandFamilies()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();

        new JsonDurableQueueCommandCodec<int>(Options).WriteEnqueue(10, writer.CreateJournalStreamWriter(new JournalStreamId(11)));
        new JsonDurableSetCommandCodec<string>(Options).WriteAdd("a", writer.CreateJournalStreamWriter(new JournalStreamId(12)));
        new JsonDurableDictionaryCommandCodec<string, int>(Options).WriteSet("alice", 42, writer.CreateJournalStreamWriter(new JournalStreamId(13)));
        new JsonDurableTaskCompletionSourceCommandCodec<int>(Options).WriteCompleted(5, writer.CreateJournalStreamWriter(new JournalStreamId(14)));

        Assert.Equal(
            """[11,["enqueue",10]]""" + "\n" +
            """[12,["add","a"]]""" + "\n" +
            """[13,["set","alice",42]]""" + "\n" +
            """[14,["completed",5]]""" + "\n",
            GetString(writer));
    }

    [Fact]
    public void JsonLinesJournalFormat_Writer_RollsBackWhenJsonWriteThrows()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        var codec = new JsonDurableListCommandCodec<ThrowingJsonValue>(Options);

        AppendValueSet(writer, 8, 42);
        Assert.Throws<InvalidOperationException>(() => codec.WriteAdd(new("bad"), writer.CreateJournalStreamWriter(new JournalStreamId(9))));
        AppendValueSet(writer, 10, 43);

        Assert.Equal(
            """[8,["set",42]]""" + "\n" +
            """[10,["set",43]]""" + "\n",
            GetString(writer));
    }

    [Fact]
    public void JsonValueCodec_JournalStreamWriterOverload_WritesPayloadFragmentBytesForNonJsonWriter()
    {
        using var writer = new CapturingNonJsonJournalBufferWriter();
        var codec = new JsonDurableValueCommandCodec<int>(Options);

        codec.WriteSet(42, writer.CreateJournalStreamWriter(new JournalStreamId(8)));

        var payload = """["set",42]"""u8.ToArray();
        var entry = Assert.Single(writer.Entries);

        Assert.Equal((uint)8, entry.StreamId.Value);
        Assert.Equal(payload, entry.Payload);
    }

    [Fact]
    public void JsonCommandWriter_AbortsEntryWhenJsonWriteThrows()
    {
        using var writer = new CapturingNonJsonJournalBufferWriter();
        var valueCodec = new JsonDurableValueCommandCodec<int>(Options);
        var throwingCodec = new JsonDurableValueCommandCodec<ThrowingJsonValue>(Options);

        valueCodec.WriteSet(42, writer.CreateJournalStreamWriter(new JournalStreamId(8)));
        Assert.Throws<InvalidOperationException>(() => throwingCodec.WriteSet(new("bad"), writer.CreateJournalStreamWriter(new JournalStreamId(9))));
        valueCodec.WriteSet(43, writer.CreateJournalStreamWriter(new JournalStreamId(10)));

        Assert.Collection(
            writer.Entries,
            entry =>
            {
                Assert.Equal((uint)8, entry.StreamId.Value);
                Assert.Equal("""["set",42]""", Encoding.UTF8.GetString(entry.Payload));
            },
            entry =>
            {
                Assert.Equal((uint)10, entry.StreamId.Value);
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
            """[8,["set",42]]""" + "\n" +
            """[10,["set",43]]""" + "\n",
            GetString(writer));
    }

    [Fact]
    public void JsonLinesJournalFormat_Writer_AppendsEntryPayloadWithoutReformatting()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        using var entry = writer.CreateJournalStreamWriter(new JournalStreamId(8)).BeginEntry();

        entry.Writer.Write("""["set" , { "value" : 42 }]"""u8);
        entry.Commit();

        Assert.Equal("""[8,["set" , { "value" : 42 }]]""" + "\n", GetString(writer));
    }

    [Fact]
    public void JsonLinesJournalFormat_Writer_Reset_ReusesBuffer()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();

        AppendValueSet(writer, 8, 42);
        writer.Reset();
        AppendValueSet(writer, 9, 43);

        Assert.Equal("""[9,["set",43]]""" + "\n", GetString(writer));
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_DispatchesEntries()
    {
        var format = new JsonLinesJournalFormat();
        var entries = Read(format, """[8,["set",42]]""" + "\n");
        var entry = Assert.Single(entries);
        var valueCodec = new JsonDurableValueCommandCodec<int>(Options);
        var consumer = new ValueConsumer<int>();

        valueCodec.Apply(CodecTestHelpers.ReadBuffer(entry.Payload), consumer);

        Assert.Equal((uint)8, entry.StreamId.Value);
        Assert.Equal(42, consumer.Value);
    }

    [Theory]
    [InlineData("\n", "blank lines")]
    [InlineData("""{"streamId":8,"entry":["set",42]}""" + "\n", "must be a JSON array")]
    [InlineData("null\n", "must be a JSON array")]
    [InlineData("[]\n", "stream id")]
    [InlineData("[8]\n", "entry payload")]
    [InlineData("""["8",["set",42]]""" + "\n", "unsigned 32-bit integer")]
    [InlineData("[8,null]\n", "entry payload array")]
    [InlineData("""[8,["set",42]]{}""" + "\n", "invalid JSON")]
    [InlineData("""[8,["set",42]""" + "\n", "invalid JSON")]
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
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("""[8,["set",42]]""" + "\n")).ToArray();

        var exception = Assert.Throws<InvalidOperationException>(() => Read(format, bytes));

        Assert.Contains("byte order marks", exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_FinalLineWithoutNewline_Throws()
    {
        var format = new JsonLinesJournalFormat();
        var jsonLines =
            """[8,["set",42]]""" + "\n" +
            """[9,["set",43]""";

        var exception = Assert.Throws<InvalidOperationException>(() => Read(format, jsonLines));

        Assert.Contains("must end with a newline", exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_ErrorMessageReportsByteOffsetOfFailingLine()
    {
        var format = new JsonLinesJournalFormat();
        var firstLine = """[8,["set",42]]""" + "\n";
        var jsonLines = firstLine + """[9,null]""" + "\n";

        var exception = Assert.Throws<InvalidOperationException>(() => Read(format, jsonLines));

        var expectedOffset = Encoding.UTF8.GetByteCount(firstLine);
        Assert.Contains($"byte offset {expectedOffset}", exception.Message);
        Assert.Contains("entry payload array", exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_ReportsByteOffsetForBlankLineMidStream()
    {
        var format = new JsonLinesJournalFormat();
        var firstLine = """[8,["set",42]]""" + "\n";
        var jsonLines = firstLine + "   \n";

        var exception = Assert.Throws<InvalidOperationException>(() => Read(format, jsonLines));

        var expectedOffset = Encoding.UTF8.GetByteCount(firstLine);
        Assert.Contains($"byte offset {expectedOffset}", exception.Message);
        Assert.Contains("blank lines", exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_ReportsByteOffsetForFinalLineMissingNewline()
    {
        var format = new JsonLinesJournalFormat();
        var firstLine = """[8,["set",42]]""" + "\n";
        var jsonLines = firstLine + """[9,["set",43]""";

        var exception = Assert.Throws<InvalidOperationException>(() => Read(format, jsonLines));

        var expectedOffset = Encoding.UTF8.GetByteCount(firstLine);
        Assert.Contains($"byte offset {expectedOffset}", exception.Message);
        Assert.Contains("must end with a newline", exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_PartialLineWaitsForNewlineWhenInputIsNotCompleted()
    {
        var format = new JsonLinesJournalFormat();
        var bytes = Encoding.UTF8.GetBytes("""[8,["set",42]]""");
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new JournalBufferReader(buffer.Reader, isCompleted: false);
        var consumer = new RecordingJournalEntrySink();

        var context = JournalTestReplayContext.Create(JsonJournalExtensions.JournalFormatKey, consumer.Bind(8));
        format.Replay(reader, context);

        Assert.Equal(bytes.Length, reader.Length);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_NewlineAtArcBufferPageBoundary_Parses()
    {
        var format = new JsonLinesJournalFormat();
        var prefix = "[8,[\"set\",\"";
        var suffix = "\"]]";
        var text = new string('a', ArcBufferWriter.MinimumPageSize - Encoding.UTF8.GetByteCount(prefix + suffix));
        var line = prefix + text + suffix;
        Assert.Equal(ArcBufferWriter.MinimumPageSize, Encoding.UTF8.GetByteCount(line));
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new JournalBufferReader(buffer.Reader, isCompleted: false);
        var consumer = new RecordingJournalEntrySink();

        var context = JournalTestReplayContext.Create(JsonJournalExtensions.JournalFormatKey, consumer.Bind(8));
        format.Replay(reader, context);

        Assert.Equal(0, reader.Length);
        var entry = Assert.Single(consumer.Entries);
        Assert.Equal((uint)8, entry.StreamId.Value);
        Assert.Equal($$"""["set","{{text}}"]""", Encoding.UTF8.GetString(entry.Payload));
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_MultiPagePartialLineWaitsForNewlineWhenInputIsNotCompleted()
    {
        var format = new JsonLinesJournalFormat();
        var prefix = "[8,[\"set\",\"";
        var suffix = "\"]]";
        var text = new string('a', ArcBufferWriter.MinimumPageSize + 8 - Encoding.UTF8.GetByteCount(prefix + suffix));
        var bytes = Encoding.UTF8.GetBytes(prefix + text + suffix);
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new JournalBufferReader(buffer.Reader, isCompleted: false);
        var consumer = new RecordingJournalEntrySink();

        var context = JournalTestReplayContext.Create(JsonJournalExtensions.JournalFormatKey, consumer.Bind(8));
        format.Replay(reader, context);

        Assert.Equal(bytes.Length, reader.Length);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_ParsesCrLfLines()
    {
        var format = new JsonLinesJournalFormat();
        var entries = Read(format, """[8,["set",42]]""" + "\r\n");
        var entry = Assert.Single(entries);

        Assert.Equal((uint)8, entry.StreamId.Value);
        Assert.Equal("""["set",42]""", Encoding.UTF8.GetString(entry.Payload));
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_ActiveState_UsesStateReplayEntry()
    {
        var format = new JsonLinesJournalFormat();
        var codec = new RecordingJsonDurableValueCommandCodec();
        var state = new RecordingState(codec);

        ReadOne(format, """[8,["set",42]]""" + "\n", state);

        Assert.Same(state, codec.Consumer);
        Assert.Equal(42, codec.Value);
        Assert.Equal(42, state.Value);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_ActiveState_PropagatesReplayEntryFailure()
    {
        var format = new JsonLinesJournalFormat();
        var state = new ThrowingState();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReadOne(format, """[8,["set",42]]""" + "\n", state));

        Assert.Equal("boom", exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_ActiveState_UsesBuiltInCodec()
    {
        var format = new JsonLinesJournalFormat();
        var state = new RecordingState(new JsonDurableValueCommandCodec<int>(Options));

        ReadOne(format, """[8,["set",42]]""" + "\n", state);

        Assert.Equal(42, state.Value);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_DurableNothingIgnoresEntryWithoutJsonCodec()
    {
        var format = new JsonLinesJournalFormat();
        var state = new NoOpState();

        ReadOne(format, """[8,["set",42]]""" + "\n", state);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_DispatchPath_RejectsTrailingTokensAfterArray()
    {
        var format = new JsonLinesJournalFormat();
        var codec = new RecordingJsonDurableValueCommandCodec();
        var state = new RecordingState(codec);

        // Dispatch path: codec.Apply -> EnsureEnd must reject any token after the closing ']'.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReadOne(format, """[8,["set",42]][9,["set",43]]""" + "\n", state));

        Assert.Contains("invalid JSON", exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_DurableNothingPath_RejectsTrailingTokensAfterArray()
    {
        var format = new JsonLinesJournalFormat();
        var state = new NoOpState();

        // SkipToEnd for IDurableNothing must reject trailing JSON, not silently ignore it.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReadOne(format, """[8,["set",42]][9,["set",43]]""" + "\n", state));

        Assert.Contains("invalid JSON", exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Read_RejectsTrailingTokensAfterArray()
    {
        var format = new JsonLinesJournalFormat();

        // The dispatch parser must reject trailing JSON after the entry array.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Read(format, """[8,["set",42]][9,["set",43]]""" + "\n"));

        Assert.Contains("invalid JSON", exception.Message);
    }

    [Fact]
    public void JsonLinesJournalFormat_Writer_AppendsDataOnlyPreservedOperation()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        var journalWriter = writer.CreateJournalStreamWriter(new JournalStreamId(8));

        journalWriter.AppendPreservedEntry(new TestPreservedJournalEntry(
            JsonJournalExtensions.JournalFormatKey,
            Encoding.UTF8.GetBytes("""["set",42]""")));

        using var slice = writer.GetBuffer();
        Assert.Equal("""[8,["set",42]]""" + "\n", Encoding.UTF8.GetString(slice.AsReadOnlySequence().ToArray()));
    }

    [Fact]
    public void JsonLinesJournalFormat_Writer_AppendsRecoveredPreservedOperationPayloadAsEntryElements()
    {
        var format = new JsonLinesJournalFormat();
        var recoveredEntry = Assert.Single(Read(format, """[8,["set",42]]""" + "\n"));
        using var writer = format.CreateWriter();
        var journalWriter = writer.CreateJournalStreamWriter(new JournalStreamId(9));

        journalWriter.AppendPreservedEntry(new TestPreservedJournalEntry(
            JsonJournalExtensions.JournalFormatKey,
            recoveredEntry.Payload));

        using var slice = writer.GetBuffer();
        Assert.Equal("""[9,["set",42]]""" + "\n", Encoding.UTF8.GetString(slice.AsReadOnlySequence().ToArray()));
    }

    [Fact]
    public void JsonLinesJournalFormat_Writer_AppendsPreservedOperationPayloadWithoutReformatting()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        var journalWriter = writer.CreateJournalStreamWriter(new JournalStreamId(8));

        journalWriter.AppendPreservedEntry(new TestPreservedJournalEntry(
            JsonJournalExtensions.JournalFormatKey,
            Encoding.UTF8.GetBytes("""["set" , { "value" : 42 }]""")));

        using var slice = writer.GetBuffer();
        Assert.Equal("""[8,["set" , { "value" : 42 }]]""" + "\n", Encoding.UTF8.GetString(slice.AsReadOnlySequence().ToArray()));
    }

    [Fact]
    public void JsonLinesJournalFormat_Writer_RejectsWrongPreservedEntryFormat()
    {
        var format = new JsonLinesJournalFormat();
        using var writer = format.CreateWriter();
        var journalWriter = writer.CreateJournalStreamWriter(new JournalStreamId(8));

        var exception = Assert.Throws<InvalidOperationException>(() => journalWriter.AppendPreservedEntry(new TestPreservedJournalEntry(
            "other",
            Encoding.UTF8.GetBytes("""["set",42]"""))));

        Assert.Contains("cannot append preserved entry", exception.Message, StringComparison.Ordinal);
    }

    private static void Apply<T>(IDurableListCommandCodec<T> codec, Action<JournalStreamWriter> write, IDurableListCommandHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static void Apply<T>(IDurableQueueCommandCodec<T> codec, Action<JournalStreamWriter> write, IDurableQueueCommandHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static void Apply<T>(IDurableSetCommandCodec<T> codec, Action<JournalStreamWriter> write, IDurableSetCommandHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static void Apply<T>(IPersistentStateCommandCodec<T> codec, Action<JournalStreamWriter> write, IPersistentStateCommandHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static void Apply<T>(IDurableTaskCompletionSourceCommandCodec<T> codec, Action<JournalStreamWriter> write, IDurableTaskCompletionSourceCommandHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.ReadBuffer(CodecTestHelpers.WriteEntry(write)), consumer);
    }

    private static byte[] AssertPayload(string expectedJson, Action<JournalStreamWriter> write)
    {
        var payload = CodecTestHelpers.WriteEntry(write);
        Assert.Equal(expectedJson, GetString(payload));
        return payload;
    }

    private static string GetString(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);

    private static string GetString(ReadOnlyMemory<byte> payload) => Encoding.UTF8.GetString(payload.Span);

    private static string GetString(JournalBufferWriter writer)
    {
        using var buffer = writer.GetBuffer();
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void AppendValueSet(JournalBufferWriter writer, uint streamId, int value)
    {
        var codec = new JsonDurableValueCommandCodec<int>(Options);
        codec.WriteSet(value, writer.CreateJournalStreamWriter(new JournalStreamId(streamId)));
    }

    private static JsonSerializerOptions CreateOptions() => new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    private static List<RecordedJournalEntry> Read(JsonLinesJournalFormat format, string jsonLines) => Read(format, Encoding.UTF8.GetBytes(jsonLines));

    private static List<RecordedJournalEntry> Read(JsonLinesJournalFormat format, byte[] bytes)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        var reader = new JournalBufferReader(buffer.Reader, isCompleted: true);
        var consumer = new RecordingJournalEntrySink();
        var context = JournalTestReplayContext.Create(JsonJournalExtensions.JournalFormatKey, consumer.Bind(8, 9, 10));
        format.Replay(reader, context);
        Assert.Equal(0, reader.Length);

        return consumer.Entries;
    }

    private static void ReadOne(JsonLinesJournalFormat format, string jsonLines, IJournaledState state)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(Encoding.UTF8.GetBytes(jsonLines));
        var reader = new JournalBufferReader(buffer.Reader, isCompleted: true);
        var context = JournalTestReplayContext.Create(JsonJournalExtensions.JournalFormatKey, (new JournalStreamId(8), state));
        format.Replay(reader, context);
        Assert.Equal(0, reader.Length);
    }

    private sealed record RecordedJournalEntry(JournalStreamId StreamId, byte[] Payload);

    private sealed class CapturingNonJsonJournalBufferWriter : JournalBufferWriter
    {
        private readonly List<RecordedJournalEntryLength> _entries = [];

        public List<RecordedJournalEntry> Entries
        {
            get
            {
                using var buffer = GetBuffer();
                var bytes = buffer.ToArray();
                var offset = 0;
                var result = new List<RecordedJournalEntry>(_entries.Count);
                foreach (var entry in _entries)
                {
                    result.Add(new(entry.StreamId, bytes.AsSpan(offset, entry.Length).ToArray()));
                    offset += entry.Length;
                }

                return result;
            }
        }

        protected override void FinishEntry(JournalStreamId streamId)
        {
            _entries.Add(new(streamId, ActiveEntryLength));
        }

        protected override void WritePreservedEntry(JournalStreamId streamId, IPreservedJournalEntry entry) =>
            throw new InvalidOperationException("This test writer does not accept preserved entries.");
    }

    private sealed record RecordedJournalEntryLength(JournalStreamId StreamId, int Length);

    private sealed class RecordingJournalEntrySink
    {
        public List<RecordedJournalEntry> Entries { get; } = [];

        public (JournalStreamId StreamId, IJournaledState State)[] Bind(params uint[] streamIds)
        {
            var bindings = new (JournalStreamId StreamId, IJournaledState State)[streamIds.Length];
            for (var i = 0; i < streamIds.Length; i++)
            {
                var streamId = new JournalStreamId(streamIds[i]);
                bindings[i] = (streamId, new StreamSink(this, streamId));
            }

            return bindings;
        }

        private sealed class StreamSink(RecordingJournalEntrySink owner, JournalStreamId streamId) : IJournaledState
        {
            void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
                owner.Entries.Add(new(streamId, entry.Reader.ToArray()));

            public void Reset(JournalStreamWriter storage) { }
            public void AppendEntries(JournalStreamWriter writer) { }
            public void AppendSnapshot(JournalStreamWriter writer) { }
            public IJournaledState DeepCopy() => throw new NotSupportedException();
        }
    }

    private sealed class RecordingState(IDurableValueCommandCodec<int> codec) : IJournaledState, IDurableValueCommandHandler<int>
    {
        public int? Value { get; private set; }

        void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, codec).Apply(entry.Reader, this);

        public void ApplySet(int value) => Value = value;

        public void Reset(JournalStreamWriter storage) { }
        public void AppendEntries(JournalStreamWriter writer) { }
        public void AppendSnapshot(JournalStreamWriter writer) { }
        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class NoOpState : IDurableNothing, IJournaledState
    {
        void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) { }

        public void Reset(JournalStreamWriter storage) { }
        public void AppendEntries(JournalStreamWriter writer) { }
        public void AppendSnapshot(JournalStreamWriter writer) { }
        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class ThrowingState : IJournaledState
    {
        void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            throw new InvalidOperationException("boom");

        public void Reset(JournalStreamWriter storage) { }
        public void AppendEntries(JournalStreamWriter writer) { }
        public void AppendSnapshot(JournalStreamWriter writer) { }
        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class RecordingJsonDurableValueCommandCodec : IDurableValueCommandCodec<int>
    {
        public IDurableValueCommandHandler<int>? Consumer { get; private set; }

        public int? Value { get; private set; }

        public void WriteSet(int value, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(JournalBufferReader input, IDurableValueCommandHandler<int> consumer)
        {
            using var reader = new JsonCommandReader(input);
            Consumer = consumer;
            Value = reader.Deserialize(1, JsonJournalEntryFields.Value, JsonCodecTestJsonContext.Default.Int32);
            reader.EnsureEnd(2);
            consumer.ApplySet(Value.GetValueOrDefault());
        }
    }

    private sealed class TestPreservedJournalEntry(string formatKey, ReadOnlyMemory<byte> payload) : IPreservedJournalEntry
    {
        public ReadOnlyMemory<byte> Payload { get; } = payload.ToArray();

        public string FormatKey { get; } = formatKey;
    }

    private sealed class DictionaryConsumer<TKey, TValue> : IDurableDictionaryCommandHandler<TKey, TValue> where TKey : notnull
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

    private sealed class ListConsumer<T> : IDurableListCommandHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplySet(int index, T item) => Commands.Add($"set:{index}:{item}");
        public void ApplyInsert(int index, T item) => Commands.Add($"insert:{index}:{item}");
        public void ApplyRemoveAt(int index) => Commands.Add($"remove:{index}");
        public void ApplyClear() => Commands.Add("clear");
        public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
    }

    private sealed class QueueConsumer<T> : IDurableQueueCommandHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyEnqueue(T item) => Commands.Add($"enqueue:{item}");
        public void ApplyDequeue() => Commands.Add("dequeue");
        public void ApplyClear() => Commands.Add("clear");
        public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
    }

    private sealed class SetConsumer<T> : IDurableSetCommandHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplyRemove(T item) => Commands.Add($"remove:{item}");
        public void ApplyClear() => Commands.Add("clear");
        public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
    }

    private sealed class ValueConsumer<T> : IDurableValueCommandHandler<T>
    {
        public T? Value { get; private set; }
        public void ApplySet(T value) => Value = value;
    }

    private sealed class StateConsumer<T> : IPersistentStateCommandHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplySet(T state, ulong version) => Commands.Add($"set:{state}:{version}");
        public void ApplyClear() => Commands.Add("clear");
    }

    private sealed class TcsConsumer<T> : IDurableTaskCompletionSourceCommandHandler<T>
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
