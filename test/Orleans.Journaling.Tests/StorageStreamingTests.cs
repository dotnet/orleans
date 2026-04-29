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
        var storage = new VolatileStateMachineStorage("custom-format");
        var segmentBytes = new byte[] { 1, 2, 3, 4 };
        var snapshotBytes = new byte[] { 5, 6, 7 };

        using (var segmentData = CreateBuffer(segmentBytes))
        {
            await storage.AppendAsync(segmentData, CancellationToken.None);
        }

        using (var snapshotData = CreateBuffer(snapshotBytes))
        {
            await storage.ReplaceAsync(snapshotData, CancellationToken.None);
        }

        Assert.Equal("custom-format", storage.LogFormatKey);
        Assert.Single(storage.Segments);
        Assert.Equal(snapshotBytes, storage.Segments[0]);
    }

    [Fact]
    public async Task VolatileStorage_ReadsRawBuffersWithoutFormatDecoding()
    {
        var storage = new VolatileStateMachineStorage("custom-format");
        var rawBytes = new byte[] { 255, 0, 1 };
        var consumer = new CapturingLogDataConsumer();

        using (var data = CreateBuffer(rawBytes))
        {
            await storage.AppendAsync(data, CancellationToken.None);
        }

        await storage.ReadAsync(consumer, CancellationToken.None);

        var buffer = Assert.Single(consumer.Buffers);
        Assert.Equal(rawBytes, buffer);
    }

    [Fact]
    public async Task VolatileStorage_DisposesReadBufferAfterCallbackReturns()
    {
        var storage = new VolatileStateMachineStorage();
        var rawBytes = new byte[] { 1, 2, 3 };
        var consumer = new RetainingLogDataConsumer();

        using (var data = CreateBuffer(rawBytes))
        {
            await storage.AppendAsync(data, CancellationToken.None);
        }

        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => consumer.RetainedBuffer.ToArray());
    }

    [Fact]
    public async Task VolatileStorage_ReadConsumerCanRetainPinnedSlice()
    {
        var storage = new VolatileStateMachineStorage();
        var rawBytes = new byte[] { 1, 2, 3 };
        using var consumer = new PinningLogDataConsumer();

        using (var data = CreateBuffer(rawBytes))
        {
            await storage.AppendAsync(data, CancellationToken.None);
        }

        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Equal(rawBytes, consumer.RetainedBuffer.ToArray());
    }

    [Fact]
    public void BinaryFormatRead_RejectsTruncatedFixed32Frame()
    {
        using var data = CreateBuffer([10, 0, 0, 0, 1, 2]);
        var consumer = new CapturingLogEntryConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ((IStateMachineLogFormat)BinaryLogExtentCodec.Instance).Read(data, consumer));

        Assert.Contains("exceeds remaining input bytes", exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
    }

    private static ArcBuffer CreateBuffer(ReadOnlySpan<byte> value)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(value);
        return buffer.ConsumeSlice(buffer.Length);
    }

    private sealed class CapturingLogDataConsumer : IStateMachineLogDataConsumer
    {
        public List<byte[]> Buffers { get; } = [];

        public void OnLogData(ArcBuffer data) => Buffers.Add(data.ToArray());
    }

    private sealed class CapturingLogEntryConsumer : IStateMachineLogEntryConsumer
    {
        public List<(StateMachineId StreamId, byte[] Payload)> Entries { get; } = [];

        public void OnEntry(StateMachineId streamId, ReadOnlySequence<byte> payload) => Entries.Add(new(streamId, payload.ToArray()));
    }

    private sealed class RetainingLogDataConsumer : IStateMachineLogDataConsumer
    {
        public ArcBuffer RetainedBuffer { get; private set; }

        public void OnLogData(ArcBuffer data) => RetainedBuffer = data;
    }

    private sealed class PinningLogDataConsumer : IStateMachineLogDataConsumer, IDisposable
    {
        public ArcBuffer RetainedBuffer { get; private set; }

        public void OnLogData(ArcBuffer data) => RetainedBuffer = data.Slice(0, data.Length);

        public void Dispose() => RetainedBuffer.Dispose();
    }
}
