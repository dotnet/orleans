using System.Buffers;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class StorageStreamingTests
{
    [Fact]
    public async Task VolatileStorage_AppendAndReplaceStoreRawBuffers()
    {
        var storage = new VolatileLogStorage();
        var segmentBytes = new byte[] { 1, 2, 3, 4 };
        var snapshotBytes = new byte[] { 5, 6, 7 };

        using (var segmentData = CreateBuffer(segmentBytes))
        {
            await storage.AppendAsync(segmentData.AsReadOnlySequence(), CancellationToken.None);
        }

        using (var snapshotData = CreateBuffer(snapshotBytes))
        {
            await storage.ReplaceAsync(snapshotData.AsReadOnlySequence(), CancellationToken.None);
        }

        Assert.Single(storage.Segments);
        Assert.Equal(snapshotBytes, storage.Segments[0]);
    }

    [Fact]
    public async Task VolatileStorage_ReadsRawBuffersWithoutFormatDecoding()
    {
        var storage = new VolatileLogStorage();
        var rawBytes = new byte[] { 255, 0, 1 };
        using var buffer = new ArcBufferWriter();
        var buffers = new List<byte[]>();

        using (var data = CreateBuffer(rawBytes))
        {
            await storage.AppendAsync(data.AsReadOnlySequence(), CancellationToken.None);
        }

        await storage.ReadAsync(buffer, reader =>
        {
            using var data = reader.PeekSlice(reader.Length);
            buffers.Add(data.ToArray());
            reader.Skip(reader.Length);
        }, CancellationToken.None);

        var captured = Assert.Single(buffers);
        Assert.Equal(rawBytes, captured);
    }

    [Fact]
    public async Task VolatileStorage_AppendsReadsIntoCallerOwnedBuffer()
    {
        var storage = new VolatileLogStorage();
        var rawBytes = new byte[] { 1, 2, 3 };
        using var buffer = new ArcBufferWriter();

        using (var data = CreateBuffer(rawBytes))
        {
            await storage.AppendAsync(data.AsReadOnlySequence(), CancellationToken.None);
        }

        await storage.ReadAsync(buffer, _ => { }, CancellationToken.None);

        using var retained = buffer.PeekSlice(buffer.Length);
        Assert.Equal(rawBytes, retained.ToArray());
    }

    [Fact]
    public async Task VolatileStorage_ReadConsumerCanRetainPinnedSlice()
    {
        var storage = new VolatileLogStorage();
        var rawBytes = new byte[] { 1, 2, 3 };
        using var buffer = new ArcBufferWriter();
        ArcBuffer retainedBuffer = default;

        using (var data = CreateBuffer(rawBytes))
        {
            await storage.AppendAsync(data.AsReadOnlySequence(), CancellationToken.None);
        }

        try
        {
            await storage.ReadAsync(buffer, reader => retainedBuffer = reader.PeekSlice(reader.Length), CancellationToken.None);

            Assert.Equal(rawBytes, retainedBuffer.ToArray());
        }
        finally
        {
            retainedBuffer.Dispose();
        }
    }

    [Fact]
    public void BinaryFormatRead_RejectsTruncatedFixed32Frame()
    {
        using var writer = CreateWriter([10, 0, 0, 0, 1, 2]);
        var reader = new ArcBufferReader(writer);
        var consumer = new CapturingLogEntrySink();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(reader, consumer, isCompleted: true));

        Assert.Contains("exceeds remaining input bytes", exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void BinaryFormatRead_WaitsForIncompleteFrameWhenInputIsNotCompleted()
    {
        using var writer = CreateWriter([10, 0, 0, 0, 1, 2]);
        var reader = new ArcBufferReader(writer);
        var consumer = new CapturingLogEntrySink();

        var result = ((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(reader, consumer, isCompleted: false);

        Assert.False(result);
        Assert.Equal(6, reader.Length);
        Assert.Empty(consumer.Entries);
    }

    [Fact]
    public void BinaryFormatRead_ConsumesCompletePrefixAndWaitsForPartialSuffix()
    {
        using var buffer = new LogSegmentBuffer();
        AppendEntry(buffer.CreateLogWriter(new LogStreamId(8)), [1, 2, 3]);
        using var committed = buffer.GetCommittedBuffer();
        var entryBytes = committed.ToArray();
        using var data = CreateWriter([.. entryBytes, 10, 0]);
        var reader = new ArcBufferReader(data);
        var consumer = new CapturingLogEntrySink();

        var firstResult = ((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(reader, consumer, isCompleted: false);
        var secondResult = ((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(reader, consumer, isCompleted: false);

        var entry = Assert.Single(consumer.Entries);
        Assert.Equal((ulong)8, entry.StreamId.Value);
        Assert.Equal([1, 2, 3], entry.Payload);
        Assert.True(firstResult);
        Assert.False(secondResult);
        Assert.Equal(2, reader.Length);
    }

    private static void AppendEntry(LogWriter writer, ReadOnlySpan<byte> payload)
    {
        using var entry = writer.BeginEntry();
        entry.Writer.Write(payload);
        entry.Commit();
    }

    private static ArcBuffer CreateBuffer(ReadOnlySpan<byte> value)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(value);
        return buffer.ConsumeSlice(buffer.Length);
    }

    private static ArcBufferWriter CreateWriter(ReadOnlySpan<byte> value)
    {
        var buffer = new ArcBufferWriter();
        buffer.Write(value);
        return buffer;
    }

    private sealed class CapturingLogEntrySink : ILogEntrySink
    {
        public List<(LogStreamId StreamId, byte[] Payload)> Entries { get; } = [];

        public void OnEntry(LogStreamId streamId, ReadOnlySequence<byte> payload) => Entries.Add(new(streamId, payload.ToArray()));
    }

}
