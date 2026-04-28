using System.Buffers.Binary;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class StorageStreamingTests
{
    [Fact]
    public async Task VolatileStorage_AppendAndReplaceUseStreamEncoding()
    {
        var storage = new VolatileStateMachineStorage(new StreamOnlyBinaryCodec());
        using var segment = CreateSegment();
        using var snapshot = CreateSegment();

        await storage.AppendAsync(segment, CancellationToken.None);
        await storage.ReplaceAsync(snapshot, CancellationToken.None);

        Assert.Single(storage.Segments);
    }

    [Fact]
    public void BinaryCodec_StreamEncodingMatchesArrayEncoding()
    {
        using var segment = CreateSegment();
        var expected = BinaryLogExtentCodec.Instance.Encode(segment);

        using var stream = BinaryLogExtentCodec.Instance.EncodeToStream(segment);
        using var output = new MemoryStream();
        stream.CopyTo(output);

        Assert.Equal(expected, output.ToArray());
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(0, sizeof(uint))));
    }

    [Fact]
    public void BinaryCodec_RejectsTruncatedFixed32Frame()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write([10, 0, 0, 0, 1, 2]);
        using var extent = BinaryLogExtentCodec.Instance.Decode(buffer.ConsumeSlice(buffer.Length));

        Assert.Throws<InvalidOperationException>(() => extent.Entries.ToList());
    }

    private static LogExtentBuilder CreateSegment()
    {
        var segment = new LogExtentBuilder();
        var writer = segment.BeginEntry(new StateMachineId(1));
        writer.Write([1, 2, 3]);
        writer.Commit();
        return segment;
    }

    private sealed class StreamOnlyBinaryCodec : IStateMachineLogExtentCodec
    {
        public byte[] Encode(LogExtentBuilder value) => throw new InvalidOperationException("Storage should use stream encoding.");

        public Stream EncodeToStream(LogExtentBuilder value) => BinaryLogExtentCodec.Instance.EncodeToStream(value);

        public LogExtent Decode(ArcBuffer value) => BinaryLogExtentCodec.Instance.Decode(value);
    }
}
