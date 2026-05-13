using System.Buffers;
using System.Text;
using Orleans.Journaling.Json;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class JournalWriterBufferOwnershipTests
{
    private const string BinaryFormat = OrleansBinaryJournalFormat.JournalFormatKey;
    private const string JsonFormat = JsonJournalExtensions.JournalFormatKey;

    [Theory]
    [InlineData(BinaryFormat)]
    [InlineData(JsonFormat)]
    public void GetCommittedBuffer_RemainsReadableAfterResetAndReuse(string format)
    {
        using var writer = CreateWriter(format);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), GetFirstPayload(format));
        using var committed = writer.GetCommittedBuffer();
        var expected = committed.ToArray();

        writer.Reset();
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(2)), GetSecondPayload(format));

        Assert.Equal(expected, committed.ToArray());
        Assert.NotEqual(expected, ToArray(writer));
    }

    [Theory]
    [InlineData(BinaryFormat)]
    [InlineData(JsonFormat)]
    public void GetCommittedBuffer_RemainsReadableAfterWriterDispose(string format)
    {
        var writer = CreateWriter(format);
        AppendEntry(writer.CreateJournalStreamWriter(new JournalStreamId(1)), GetFirstPayload(format));
        var committed = writer.GetCommittedBuffer();
        var expected = committed.ToArray();

        writer.Dispose();

        using (committed)
        {
            Assert.Equal(expected, committed.ToArray());
        }
    }

    private static JournalWriter CreateWriter(string format) => format switch
    {
        BinaryFormat => new OrleansBinaryJournalWriter(),
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

    private static byte[] ToArray(JournalWriter writer)
    {
        using var committed = writer.GetCommittedBuffer();
        return committed.ToArray();
    }
}
