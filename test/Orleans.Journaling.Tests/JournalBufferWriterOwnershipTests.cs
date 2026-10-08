using System.Buffers;
using System.Text;
using Orleans.Journaling.Json;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class JournalBufferWriterOwnershipTests
{
    private const string BinaryFormat = OrleansBinaryJournalFormat.JournalFormatKey;
    private const string JsonFormat = JsonLinesJournalFormat.JournalFormatKey;

    [Theory]
    [InlineData(BinaryFormat)]
    [InlineData(JsonFormat)]
    public void GetBuffer_RemainsReadableAfterResetAndReuse(string format)
    {
        using var writer = CreateWriter(format);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), GetFirstPayload(format));
        using var committed = writer.GetBuffer();
        var expected = committed.ToArray();

        writer.Reset();
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(2)), GetSecondPayload(format));

        Assert.Equal(expected, committed.ToArray());
        Assert.NotEqual(expected, ToArray(writer));
    }

    [Theory]
    [InlineData(BinaryFormat)]
    [InlineData(JsonFormat)]
    public void GetBuffer_RemainsReadableAfterWriterDispose(string format)
    {
        var writer = CreateWriter(format);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), GetFirstPayload(format));
        var committed = writer.GetBuffer();
        var expected = committed.ToArray();

        writer.Dispose();

        using (committed)
        {
            Assert.Equal(expected, committed.ToArray());
        }
    }

    [Theory]
    [InlineData(BinaryFormat)]
    [InlineData(JsonFormat)]
    public void GetBuffer_ReturnsCommittedPrefixWhileEntryIsActive(string format)
    {
        using var writer = CreateWriter(format);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), GetFirstPayload(format));
        var expectedCommittedPrefix = ToArray(writer);

        using var entry = writer.CreateJournalStreamWriter(new JournalStreamId(2)).BeginEntry();
        entry.Writer.Write(GetSecondPayload(format));

        using var committed = writer.GetBuffer();
        Assert.Equal(expectedCommittedPrefix, committed.ToArray());

        entry.Commit();
        Assert.Equal(GetEntriesBytes(format, (1, GetFirstPayload(format)), (2, GetSecondPayload(format))), ToArray(writer));
    }

    [Theory]
    [InlineData(BinaryFormat)]
    [InlineData(JsonFormat)]
    public void Consume_RemovesCommittedPrefixAndKeepsActiveEntry(string format)
    {
        using var writer = CreateWriter(format);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), GetFirstPayload(format));

        using var entry = writer.CreateJournalStreamWriter(new JournalStreamId(2)).BeginEntry();
        entry.Writer.Write(GetSecondPayload(format));
        using var committed = writer.GetCommittedBuffer();

        writer.Consume(committed);

        Assert.Empty(ToArray(writer));
        entry.Commit();
        Assert.Equal(GetEntriesBytes(format, (2, GetSecondPayload(format))), ToArray(writer));
    }

    [Theory]
    [InlineData(BinaryFormat)]
    [InlineData(JsonFormat)]
    public void Consume_RemovesPartialCommittedPrefixAndKeepsActiveEntry(string format)
    {
        using var writer = CreateWriter(format);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), GetFirstPayload(format));
        var firstEntryLength = ToArray(writer).Length;
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(2)), GetSecondPayload(format));

        using var entry = writer.CreateJournalStreamWriter(new JournalStreamId(3)).BeginEntry();
        entry.Writer.Write(GetThirdPayload(format));
        using var committed = writer.GetCommittedBuffer();
        using var consumed = committed.Slice(0, firstEntryLength);

        writer.Consume(consumed);

        Assert.Equal(GetEntriesBytes(format, (2, GetSecondPayload(format))), ToArray(writer));
        entry.Commit();
        Assert.Equal(GetEntriesBytes(format, (2, GetSecondPayload(format)), (3, GetThirdPayload(format))), ToArray(writer));
    }

    [Theory]
    [InlineData(BinaryFormat)]
    [InlineData(JsonFormat)]
    public void DisposeAfterPartialConsume_KeepsRemainingCommittedPrefix(string format)
    {
        using var writer = CreateWriter(format);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), GetFirstPayload(format));
        var firstEntryLength = ToArray(writer).Length;
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(2)), GetSecondPayload(format));

        using (var entry = writer.CreateJournalStreamWriter(new JournalStreamId(3)).BeginEntry())
        {
            entry.Writer.Write(GetThirdPayload(format));
            using var committed = writer.GetCommittedBuffer();
            using var consumed = committed.Slice(0, firstEntryLength);
            writer.Consume(consumed);
        }

        Assert.Equal(GetEntriesBytes(format, (2, GetSecondPayload(format))), ToArray(writer));
    }

    [Fact]
    public void BeginEntry_WhenStartEntryThrows_RemovesPartialFrameAndAllowsRetry()
    {
        using var writer = new FailingStartEntryWriter();
        var stream = writer.CreateJournalStreamWriter(new JournalStreamId(1));

        InvalidOperationException? exception = null;
        try
        {
            var entry = stream.BeginEntry();
            entry.Dispose();
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        Assert.NotNull(exception);
        Assert.Equal("start failed", exception.Message);
        Assert.Empty(ToArray(writer));

        writer.FailStart = false;
        AppendEntry(stream, [1, 2, 3]);

        Assert.Equal([1, 2, 3], ToArray(writer));
    }

    [Theory]
    [InlineData(BinaryFormat)]
    [InlineData(JsonFormat)]
    public void UnconsumedCommittedBufferRemainsAvailableForRetry(string format)
    {
        using var writer = CreateWriter(format);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), GetFirstPayload(format));
        using (var committed = writer.GetCommittedBuffer())
        {
            Assert.NotEmpty(committed.ToArray());
        }

        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(2)), GetSecondPayload(format));

        Assert.Equal(GetEntriesBytes(format, (1, GetFirstPayload(format)), (2, GetSecondPayload(format))), ToArray(writer));
    }

    [Theory]
    [InlineData(BinaryFormat)]
    [InlineData(JsonFormat)]
    public void Consume_RejectsStaleCommittedBuffer(string format)
    {
        using var writer = CreateWriter(format);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), GetFirstPayload(format));
        using var committed = writer.GetCommittedBuffer();
        writer.Consume(committed);

        var exception = Assert.Throws<InvalidOperationException>(() => writer.Consume(committed));

        Assert.Contains("committed length", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteAt_PatchesRelativeToActiveEntry()
    {
        using var writer = new ActiveEntryPatchingWriter();

        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), [1, 2]);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(2)), [3]);

        Assert.Equal([3, 1, 2, 2, 3], ToArray(writer));
    }

    [Fact]
    public void WriteAt_RejectsWritesOutsideActiveEntry()
    {
        using var writer = new OutOfRangeWriteAtWriter();
        using var entry = writer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();

        ArgumentOutOfRangeException? exception = null;
        try
        {
            entry.Commit();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            exception = ex;
        }

        Assert.NotNull(exception);
        Assert.Equal("value", exception.ParamName);
        Assert.Empty(ToArray(writer));
    }

    [Fact]
    public void GetEntryByte_ReadsRelativeToActiveEntry()
    {
        using var writer = new ActiveEntryByteReaderWriter();

        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), [1, 2, 3]);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(2)), [4, 5]);

        Assert.Equal([1, 2, 3, 1, 3, 4, 5, 4, 5], ToArray(writer));
    }

    [Fact]
    public void GetEntryByte_RejectsReadsOutsideActiveEntry()
    {
        using var writer = new OutOfRangeGetEntryByteWriter();
        using var entry = writer.CreateJournalStreamWriter(new JournalStreamId(1)).BeginEntry();

        entry.Writer.Write(new byte[] { 1 });

        ArgumentOutOfRangeException? exception = null;
        try
        {
            entry.Commit();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            exception = ex;
        }

        Assert.NotNull(exception);
        Assert.Equal("offset", exception.ParamName);
        Assert.Empty(ToArray(writer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonEmptyEntry_RetainsValidationAfterLargeOrConsumedPrefix(bool consumePrefix)
    {
        using var writer = CreateWriter(JsonFormat);
        var payload = Encoding.UTF8.GetBytes($"[\"set\",\"{new string('x', ArcBufferWriter.MinimumPageSize * 3 + 17)}\"]");
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), payload);
        using var prefix = writer.GetCommittedBuffer();
        var prefixBytes = Encoding.UTF8.GetBytes($"[1,{Encoding.UTF8.GetString(payload)}]\n");
        Assert.Equal(prefixBytes, prefix.ToArray());
        if (consumePrefix)
        {
            writer.Consume(prefix);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(2)), []));
        Assert.Equal("The JSON Lines journal entry has no entry payload.", exception.Message);
        Assert.Equal(consumePrefix ? [] : prefixBytes, ToArray(writer));

        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(3)), GetThirdPayload(JsonFormat));
        var suffix = """[3,["set",3]]""" + "\n";
        Assert.Equal(Encoding.UTF8.GetBytes((consumePrefix ? "" : Encoding.UTF8.GetString(prefixBytes)) + suffix), ToArray(writer));
        Assert.Equal(prefixBytes, prefix.ToArray());
    }

    [Fact]
    public void JsonEmptyEntry_RetainsValidationOnNewAndResetWriter()
    {
        using var writer = CreateWriter(JsonFormat);
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var stream = writer.CreateJournalStreamWriter(new JournalStreamId(1));
            var exception = Assert.Throws<InvalidOperationException>(() => AppendEntry(stream, []));
            Assert.Equal("The JSON Lines journal entry has no entry payload.", exception.Message);
            Assert.Empty(ToArray(writer));

            AppendEntry(stream, GetFirstPayload(JsonFormat));
            Assert.Equal(Encoding.UTF8.GetBytes("""[1,["set",1]]""" + "\n"), ToArray(writer));
            writer.Reset();
        }
    }

    private static JournalBufferWriter CreateWriter(string format) => format switch
    {
        BinaryFormat => new OrleansBinaryJournalBufferWriter(),
        JsonFormat => new JsonLinesJournalFormat().CreateWriter(),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    private static byte[] GetFirstPayload(string format) => format switch
    {
        BinaryFormat => [1, 2, 3],
        JsonFormat => Encoding.UTF8.GetBytes("""["set",1]"""),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    private static byte[] GetSecondPayload(string format) => format switch
    {
        BinaryFormat => [4, 5],
        JsonFormat => Encoding.UTF8.GetBytes("""["set",2]"""),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    private static byte[] GetThirdPayload(string format) => format switch
    {
        BinaryFormat => [6, 7, 8],
        JsonFormat => Encoding.UTF8.GetBytes("""["set",3]"""),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    private static void AppendEntry(JournalStreamWriter writer, ReadOnlySpan<byte> payload)
    {
        using var entry = writer.BeginEntry();
        entry.Writer.Write(payload);
        entry.Commit();
    }

    private static byte[] ToArray(JournalBufferWriter writer)
    {
        using var committed = writer.GetBuffer();
        return committed.ToArray();
    }

    private static byte[] GetEntriesBytes(string format, params (uint StreamId, byte[] Payload)[] entries)
    {
        using var writer = CreateWriter(format);
        foreach (var (streamId, payload) in entries)
        {
            AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(streamId)), payload);
        }

        return ToArray(writer);
    }

    private sealed class FailingStartEntryWriter : JournalBufferWriter
    {
        public bool FailStart { get; set; } = true;

        protected override void StartEntry(JournalStreamId streamId)
        {
            if (FailStart)
            {
                var span = Output.GetSpan(1);
                span[0] = 0xFF;
                Output.Advance(1);
                throw new InvalidOperationException("start failed");
            }
        }

        protected override void FinishEntry(JournalStreamId streamId)
        {
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Consume_TrimsOversizedDrainedCapacityWithoutInvalidatingCapturedOrActiveBuffers(bool activeEntry)
    {
        using var writer = new CacheBoundWriter();
        var payload = Enumerable.Repeat((byte)42, 128 * 1024).ToArray();
        using (var entry = writer.CreateJournalStreamWriter(new(1)).BeginEntry())
        {
            payload.AsSpan().CopyTo(entry.Writer.GetSpan(payload.Length));
            entry.Writer.Advance(payload.Length);
            entry.Commit();
        }
        using var captured = writer.GetBuffer();
        Assert.Equal(payload, captured.ToArray());
        if (activeEntry)
        {
            using var entry = writer.CreateJournalStreamWriter(new(2)).BeginEntry();
            var borrowed = entry.Writer.GetMemory(256 * 1024);
            writer.Consume(captured);
            using (var empty = writer.GetBuffer()) Assert.Equal(256 * 1024, empty.First.Array.Length);
            borrowed.Span[0] = 99;
            entry.Writer.Advance(1);
            entry.Commit();
            using var next = writer.GetBuffer();
            Assert.Equal(new byte[] { 99 }, next.ToArray());
            writer.Consume(next);
        }
        else
        {
            writer.Consume(captured);
        }
        using var drained = writer.GetBuffer();
        Assert.Equal(0, drained.Length);
        Assert.Equal(16 * 1024, drained.First.Array.Length);
        Assert.Equal(payload, captured.ToArray());
    }

    [Fact]
    public void AbortedEntry_TrimsOversizedIdleCapacityAndPreservesCapture()
    {
        using var writer = new CacheBoundWriter();
        using (var entry = writer.CreateJournalStreamWriter(new(1)).BeginEntry())
        {
            entry.Writer.GetSpan(128 * 1024)[0] = 42;
            entry.Writer.Advance(1);
            entry.Commit();
        }
        using var captured = writer.GetBuffer();
        using (var entry = writer.CreateJournalStreamWriter(new(2)).BeginEntry())
        {
            // Consuming while an empty entry is active must leave its writable memory untouched.
            var borrowed = entry.Writer.GetMemory(1);
            writer.Consume(captured);
            borrowed.Span[0] = 99;
            entry.Writer.Advance(1);
            // Dispose rolls this entry back. The idle oversized tail can now be released.
        }
        using var drained = writer.GetBuffer();
        Assert.Equal(0, drained.Length);
        Assert.Equal(16 * 1024, drained.First.Array.Length);
        Assert.Equal(new byte[] { 42 }, captured.ToArray());
    }

    private sealed class CacheBoundWriter : JournalBufferWriter
    {
        protected override void FinishEntry(JournalStreamId streamId) { }
    }

    private sealed class ActiveEntryPatchingWriter : JournalBufferWriter
    {
        protected override void StartEntry(JournalStreamId streamId)
        {
            var span = Output.GetSpan(1);
            span[0] = 0;
            Output.Advance(1);
        }

        protected override void FinishEntry(JournalStreamId streamId)
        {
            Span<byte> encoded = stackalloc byte[1];
            encoded[0] = checked((byte)ActiveEntryLength);
            WriteAt(0, encoded);
        }
    }

    private sealed class OutOfRangeWriteAtWriter : JournalBufferWriter
    {
        protected override void StartEntry(JournalStreamId streamId)
        {
            var span = Output.GetSpan(1);
            span[0] = 0;
            Output.Advance(1);
        }

        protected override void FinishEntry(JournalStreamId streamId) => WriteAt(1, [0xFF]);
    }

    private sealed class ActiveEntryByteReaderWriter : JournalBufferWriter
    {
        protected override void FinishEntry(JournalStreamId streamId)
        {
            var first = GetEntryByte(0);
            var last = GetEntryByte(ActiveEntryLength - 1);
            Output.Write([first, last]);
        }
    }

    private sealed class OutOfRangeGetEntryByteWriter : JournalBufferWriter
    {
        protected override void FinishEntry(JournalStreamId streamId) => GetEntryByte(ActiveEntryLength);
    }
}
