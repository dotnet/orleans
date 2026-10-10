using System;
using System.Buffers;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
[OwnershipCodecTests(typeof(ArcBuffer))]
public sealed class ArcBufferCodecTests
{
    [Fact]
    public void LeadingEmptyPage_LastOwnerReleaseReturnsEveryPage()
    {
        using var writer = new ArcBufferWriter();
        var length = ArcBufferWriter.MinimumPageSize * 2;
        writer.GetSpan(length)[..length].Fill(0x5a);
        writer.AdvanceWriter(length);
        var original = writer.ConsumeSlice(length);
        var prefix = original.First;
        var payloadPage = prefix.Next!;
        Assert.Equal(0, prefix.Length);
        Assert.Equal(length, payloadPage.Length);
        var retained = original.Slice(0);
        try
        {
            original.Dispose();
            original = default;
            writer.Dispose();

            Assert.Equal(0, prefix.ReferenceCount);
            Assert.Equal(1, payloadPage.ReferenceCount);
            Assert.Equal(Enumerable.Repeat((byte)0x5a, length), retained.ToArray());

            retained.Dispose();
            retained = default;

            Assert.Equal(0, prefix.ReferenceCount);
            Assert.Equal(0, payloadPage.ReferenceCount);
        }
        finally
        {
            retained.Dispose();
            original.Dispose();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(50037)]
    public void Serialization_IsRepeatableAndDoesNotConsumeOrRelease(int length)
    {
        using var services = Services();
        using var source = new ArcBufferWriter();
        var expected = Bytes(length);
        source.Write(expected);
        using var value = source.PeekSlice(source.Length);
        var pages = value.IsEmpty ? new[] { value.First } : value.Pages.ToArray();
        var references = pages.Select(page => page.ReferenceCount).ToArray();
        var serializer = services.GetRequiredService<Serializer<ArcBuffer>>();
        var first = serializer.SerializeToArray(value);
        var second = serializer.SerializeToArray(value);
        Assert.Equal(first, second);
        Assert.Equal(references, pages.Select(page => page.ReferenceCount).ToArray());
        Assert.Equal(length, source.Length);
        Assert.Equal(expected, value.ToArray());
        using var decoded = serializer.Deserialize(first);
        Assert.Equal(expected, decoded.ToArray());
    }

    [Fact]
    public void Default_RoundTripsAndCopiesAsOwnerFreeEmpty()
    {
        using var services = Services();
        var serializer = services.GetRequiredService<Serializer<ArcBuffer>>();
        using var decoded = serializer.Deserialize(serializer.SerializeToArray(default));
        using var copy = services.GetRequiredService<DeepCopier>().Copy(ArcBuffer.Empty);
        Assert.True(decoded.IsEmpty);
        Assert.Null(decoded.First);
        Assert.Null(copy.First);
    }

    [Fact]
    public void DeepCopy_IndependentlyPinsEveryPageIncludingPageBackedEmpty()
    {
        using var services = Services();
        using var source = new ArcBufferWriter();
        source.Write(Bytes(50037));
        var original = source.ConsumeSlice(source.Length);
        var pages = original.Pages.ToArray();
        var originalReferences = pages.Select(page => page.ReferenceCount).ToArray();
        var copy = services.GetRequiredService<DeepCopier>().Copy(original);
        using var secondCopy = services.GetRequiredService<DeepCopier>().Copy(original);
        Assert.Equal(originalReferences.Select(count => count + 2).ToArray(), pages.Select(page => page.ReferenceCount).ToArray());
        source.Dispose();
        original.Dispose();
        Assert.All(pages, page => Assert.Equal(2, page.ReferenceCount));
        copy.Dispose();
        Assert.All(pages, page => Assert.Equal(1, page.ReferenceCount));
        Assert.Equal(Bytes(50037), secondCopy.ToArray());

        using var emptyWriter = new ArcBufferWriter();
        var empty = emptyWriter.PeekSlice(0);
        using var emptyCopy = services.GetRequiredService<DeepCopier>().Copy(empty);
        Assert.Same(empty.First, emptyCopy.First);
        Assert.Equal(3, empty.First.ReferenceCount);
        empty.Dispose();
        Assert.Equal(2, emptyCopy.First.ReferenceCount);
    }

    [Fact]
    public void ArcInput_DeserializesOwnedMultiPageSliceWithoutCopying()
    {
        using var services = Services();
        using var source = new ArcBufferWriter();
        source.Write(Bytes(50037));
        using var original = source.PeekSlice(source.Length);
        var serializer = services.GetRequiredService<Serializer<ArcBuffer>>();
        var wire = new ArcBufferWriter();
        using (var writeSession = services.GetRequiredService<SerializerSessionPool>().GetSession())
        {
            var writer = Writer.Create(wire, writeSession);
            serializer.Serialize(original, ref writer);
            writer.Commit();
        }
        var input = wire.ConsumeSlice(wire.Length);
        var pages = input.Pages.ToArray();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(input, session);
        var header = reader.ReadFieldHeader();
        var encodedLength = reader.ReadVarUInt32();
        var payloadOffset = (int)reader.Position;
        var expectedView = input.Slice(payloadOffset, (int)encodedLength);
        var references = pages.Select(page => page.ReferenceCount).ToArray();
        reader = Reader.Create(input, session);
        var value = new ArcBufferCodec().ReadValue(ref reader, reader.ReadFieldHeader());
        Assert.Same(expectedView.First, value.First);
        Assert.Equal(expectedView.Offset, value.Offset);
        Assert.Equal(expectedView.Pages.ToArray(), value.Pages.ToArray());
        Assert.All(value.Pages.ToArray(), page => Assert.Equal(references[Array.IndexOf(pages, page)] + 1, page.ReferenceCount));
        Assert.Equal(input.Length, reader.Position);
        expectedView.Dispose();
        wire.Dispose();
        input.Dispose();
        Assert.All(value.Pages.ToArray(), page => Assert.Equal(1, page.ReferenceCount));
        Assert.Equal(Bytes(50037), value.ToArray());
        value.Dispose();
    }

    [Fact]
    public void OwnedReaderSlice_HandlesGlobalOffsetsForksAndZeroInput()
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        using var source = new ArcBufferWriter();
        var expected = Bytes(50037);
        source.Write(expected);
        using var input = source.PeekSlice(source.Length);
        var reader = new Reader<ArcBufferReaderInput>(new ArcBufferReaderInput(in input), session, 113);
        reader.Skip(17000);
        Assert.True(reader.TryReadArcBuffer(18000, out var value));
        using (value)
        {
            Assert.Equal(expected.AsSpan(17000, 18000).ToArray(), value.ToArray());
            Assert.Equal(35113, reader.Position);
        }
        reader.ForkFrom(18113, out var fork);
        fork.ForkFrom(19113, out var nestedFork);
        Assert.True(nestedFork.TryReadArcBuffer(7, out var forked));
        using (forked) Assert.Equal(expected.AsSpan(19000, 7).ToArray(), forked.ToArray());

        var emptyReader = Reader.Create(default(ArcBuffer), session);
        Assert.Equal(0, emptyReader.Remaining);
        Assert.True(emptyReader.TryReadArcBuffer(0, out var empty));
        Assert.Null(empty.First);
        empty.Dispose();
        try { emptyReader.TryReadArcBuffer(1, out _); Assert.Fail("Truncated Arc input must throw."); }
        catch (IndexOutOfRangeException) { }
    }

    [Fact]
    public void NonArcInputs_CopyIntoOwnedPagesWithoutArrayIntermediate()
    {
        using var services = Services();
        var serializer = services.GetRequiredService<Serializer<ArcBuffer>>();
        using var source = new ArcBufferWriter();
        var expected = Bytes(50037);
        source.Write(expected);
        using var value = source.PeekSlice(source.Length);
        var wire = serializer.SerializeToArray(value);
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var sequenceReader = Reader.Create(new ReadOnlySequence<byte>(wire), session);
        using var sequenceValue = serializer.Deserialize(ref sequenceReader);
        session.Reset();
        using var stream = new MemoryStream(wire, writable: false);
        var streamReader = Reader.Create(stream, session);
        using var streamValue = serializer.Deserialize(ref streamReader);
        Array.Fill(wire, (byte)0);
        Assert.Equal(expected, sequenceValue.ToArray());
        Assert.Equal(expected, streamValue.ToArray());
        Assert.NotSame(value.First, sequenceValue.First);
    }

    [Fact]
    public void FailedWrites_LeaveSourceOwnedAndReadable()
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        using var source = new ArcBufferWriter();
        source.Write(Bytes(37));
        using var value = source.PeekSlice(source.Length);
        var failing = new FailingBufferWriter();
        try
        {
            var writer = Writer.Create(failing, session);
            new ArcBufferCodec().WriteField(ref writer, 0, typeof(ArcBuffer), value);
            Assert.Fail("Expected failure");
        }
        catch (IOException) { }
        Assert.True(failing.CommittedCount > 0);
        Assert.Equal(2, value.First.ReferenceCount);
        Assert.Equal(Bytes(37), value.ToArray());
    }

    [Fact]
    public void TruncatedArcPayload_DoesNotAcquireAnyPins()
    {
        using var services = Services();
        using var source = new ArcBufferWriter();
        source.Write(Bytes(50037));
        using var value = source.PeekSlice(source.Length);
        var wireBytes = services.GetRequiredService<Serializer<ArcBuffer>>().SerializeToArray(value);
        using var wire = new ArcBufferWriter();
        wire.Write(wireBytes.AsSpan(0, wireBytes.Length - 1));
        using var input = wire.PeekSlice(wire.Length);
        var pages = input.Pages.ToArray();
        var references = pages.Select(page => page.ReferenceCount).ToArray();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(input, session);
        try { new ArcBufferCodec().ReadValue(ref reader, reader.ReadFieldHeader()); Assert.Fail("Expected truncation failure"); }
        catch (IndexOutOfRangeException) { }
        Assert.Equal(references, pages.Select(page => page.ReferenceCount).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyArcInputPayload_ReturnsExactlyOneOwnedPin(bool pageBacked)
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        if (!pageBacked)
        {
            var reader = Reader.Create(ArcBuffer.Empty, session);
            Assert.True(reader.TryReadArcBuffer(0, out var empty));
            Assert.Null(empty.First);
            empty.Dispose();
            return;
        }

        using var writer = new ArcBufferWriter();
        using var input = writer.PeekSlice(0);
        var before = input.First.ReferenceCount;
        var ownedReader = Reader.Create(input, session);
        Assert.True(ownedReader.TryReadArcBuffer(0, out var result));
        Assert.Same(input.First, result.First);
        Assert.Equal(before + 1, input.First.ReferenceCount);
        result.Dispose();
        Assert.Equal(before, input.First.ReferenceCount);
    }

    [Fact]
    public void OversizedLength_IsRejectedBeforeReadingOrPinningPayload()
    {
        using var services = Services();
        using var wire = new ArcBufferWriter();
        using (var writeSession = services.GetRequiredService<SerializerSessionPool>().GetSession())
        {
            var writer = Writer.Create(wire, writeSession);
            writer.WriteFieldHeader(0, typeof(ArcBuffer), typeof(ArcBuffer), Orleans.Serialization.WireProtocol.WireType.LengthPrefixed);
            writer.WriteVarUInt32(uint.MaxValue);
            writer.Commit();
        }
        using var input = wire.PeekSlice(wire.Length);
        var before = input.First.ReferenceCount;
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(input, session);
        Exception? error = null;
        try { new ArcBufferCodec().ReadValue(ref reader, reader.ReadFieldHeader()); }
        catch (Exception exception) { error = exception; }
        Assert.IsType<IndexOutOfRangeException>(error);
        Assert.Equal(before, input.First.ReferenceCount);
    }

    [Fact]
    public void ReaderInputFailureAfterPartialCopy_PropagatesAndSourceRemainsReadable()
    {
        using var services = Services();
        using var source = new ArcBufferWriter();
        source.Write(Bytes(50037));
        using var value = source.PeekSlice(source.Length);
        var wire = services.GetRequiredService<Serializer<ArcBuffer>>().SerializeToArray(value);
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var input = new FailingReaderInput(wire);
        var reader = new Reader<ReaderInput>(input, session, 0);
        Exception? error = null;
        try { new ArcBufferCodec().ReadValue(ref reader, reader.ReadFieldHeader()); }
        catch (Exception exception) { error = exception; }
        Assert.IsType<IOException>(error);
        Assert.Equal(2, input.ByteReadCalls);
        Assert.Equal(Bytes(50037), value.ToArray());
        Assert.Equal(2, value.First.ReferenceCount);
    }

    private sealed class FailingReaderInput(byte[] input) : ReaderInput
    {
        private int _position;
        public int ByteReadCalls { get; private set; }
        public override long Position => _position;
        public override long Length => input.Length;
        public override void Skip(long count) => _position += checked((int)count);
        public override void Seek(long position) => _position = checked((int)position);
        public override byte ReadByte() => input[_position++];
        public override uint ReadUInt32() => throw new NotSupportedException();
        public override ulong ReadUInt64() => throw new NotSupportedException();
        public override void ReadBytes(Span<byte> destination)
        {
            ByteReadCalls++;
            if (ByteReadCalls == 2) throw new IOException("read failure after first pooled-page write");
            input.AsSpan(_position, destination.Length).CopyTo(destination);
            _position += destination.Length;
        }
        public override void ReadBytes(byte[] destination, int offset, int length) => ReadBytes(destination.AsSpan(offset, length));
        public override bool TryReadBytes(int length, out ReadOnlySpan<byte> bytes) { bytes = default; return false; }
    }

    private sealed class FailingBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[16];
        private bool _rented;
        public int CommittedCount { get; private set; }
        public void Advance(int count) => CommittedCount += count;
        public Memory<byte> GetMemory(int sizeHint = 0) => Rent();
        public Span<byte> GetSpan(int sizeHint = 0) => Rent().Span;
        private Memory<byte> Rent()
        {
            if (_rented) throw new IOException("write failure after first segment");
            _rented = true;
            return _buffer;
        }
    }

    internal static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 31)).ToArray();
    internal static ServiceProvider Services() => new ServiceCollection().AddSerializer().BuildServiceProvider();
}
