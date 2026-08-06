using System.Buffers;
using System.Text;
using Orleans.Journaling.Json;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class JournalBufferWriterOwnershipTests
{
    private const string BinaryFormat = OrleansBinaryJournalFormat.JournalFormatKey;
    private const string JsonFormat = JsonJournalExtensions.JournalFormatKey;

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
