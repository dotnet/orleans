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
        entry.PayloadWriter.Write(GetSecondPayload(format));

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
        entry.PayloadWriter.Write(GetSecondPayload(format));
        using var committed = writer.GetCommittedBuffer();

        writer.Consume(committed);

        Assert.Empty(ToArray(writer));
        entry.Commit();
        Assert.Equal(GetEntriesBytes(format, (2, GetSecondPayload(format))), ToArray(writer));
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

    private static void AppendEntry(JournalStreamWriter writer, ReadOnlySpan<byte> payload)
    {
        using var entry = writer.BeginEntry();
        entry.PayloadWriter.Write(payload);
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
}
