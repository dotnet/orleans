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
        var pages = value.IsEmpty ? new[] { Assert.IsType<ArcBufferPage>(value.First) } : value.Pages.ToArray();
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
    public void GeneratedContainer_UsesOwnedArcCodecAndCopier()
    {
        using var services = Services();
        using var source = new ArcBufferWriter();
        var expected = Bytes(50037);
        source.Write(expected);
        var original = source.ConsumeSlice(source.Length);
        var container = new ArcContainer { Buffer = original };
        var serializer = services.GetRequiredService<Serializer<ArcContainer>>();
        var first = serializer.SerializeToArray(container);
        var second = serializer.SerializeToArray(container);
        var decoded = Assert.IsType<ArcContainer>(serializer.Deserialize(first));
        var pages = original.Pages.ToArray();
        var references = pages.Select(page => page.ReferenceCount).ToArray();
        var copy = Assert.IsType<ArcContainer>(services.GetRequiredService<DeepCopier>().Copy(container));
        using var decodedBuffer = decoded.Buffer;
        using var copiedBuffer = copy.Buffer;
        try
        {
            Assert.Equal(first, second);
            Assert.Same(original.First, copiedBuffer.First);
            Assert.NotSame(original.First, decodedBuffer.First);
            Assert.Equal(references.Select(count => count + 1), pages.Select(page => page.ReferenceCount));
            original.Dispose();
            original = default;
            source.Dispose();
            Assert.All(pages, page => Assert.Equal(1, page.ReferenceCount));
            Assert.Equal(expected, decodedBuffer.ToArray());
            Assert.Equal(expected, copiedBuffer.ToArray());
        }
        finally
        {
            original.Dispose();
        }
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
        var emptyPage = Assert.IsType<ArcBufferPage>(empty.First);
        Assert.Equal(3, emptyPage.ReferenceCount);
        empty.Dispose();
        Assert.Equal(2, emptyPage.ReferenceCount);
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
        var value = reader.ReadOwnedBuffer(18000);
        using (value)
        {
            Assert.Equal(expected.AsSpan(17000, 18000).ToArray(), value.ToArray());
            Assert.Equal(35113, reader.Position);
        }
        reader.ForkFrom(18113, out var fork);
        fork.ForkFrom(19113, out var nestedFork);
        var forked = nestedFork.ReadOwnedBuffer(7);
        using (forked) Assert.Equal(expected.AsSpan(19000, 7).ToArray(), forked.ToArray());

        var emptyReader = Reader.Create(default(ArcBuffer), session);
        Assert.Equal(0, emptyReader.Remaining);
        var empty = emptyReader.ReadOwnedBuffer(0);
        Assert.Null(empty.First);
        empty.Dispose();
        try { emptyReader.ReadOwnedBuffer(1); Assert.Fail("Truncated Arc input must throw."); }
        catch (IndexOutOfRangeException) { }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 17000)]
    [InlineData(true, 0)]
    [InlineData(true, 17000)]
    public void ReadOwnedBuffer_ValidatedSkipReachesExactEnd(bool leadingEmptyPages, int offset)
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        using var source = new ArcBufferWriter();
        if (leadingEmptyPages)
        {
            source.GetSpan(ArcBufferWriter.MinimumPageSize * 2);
            source.GetSpan(ArcBufferWriter.MinimumPageSize * 4);
        }

        var expected = Bytes(50037);
        source.Write(expected);
        using var input = source.PeekSlice(source.Length);
        if (leadingEmptyPages)
        {
            var firstPage = Assert.IsType<ArcBufferPage>(input.First);
            Assert.Equal(0, firstPage.Length);
            Assert.Equal(0, firstPage.Next!.Length);
        }

        var pages = input.Pages.ToArray();
        var before = pages.Select(page => page.ReferenceCount).ToArray();
        var reader = new Reader<ArcBufferReaderInput>(new ArcBufferReaderInput(in input), session, 113);
        reader.Skip(offset);
        var remaining = (int)reader.Remaining;
        reader.EnsureAvailable((uint)remaining);
        var value = reader.ReadOwnedBuffer(remaining);
        try
        {
            Assert.Equal(113 + input.Length, reader.Position);
            Assert.Equal(0, reader.Remaining);
            reader.Skip(0);
            Assert.Equal(113 + input.Length, reader.Position);
            Assert.Equal(expected.AsSpan(offset).ToArray(), value.ToArray());
            var ownedPages = value.Pages.ToArray();
            for (var i = 0; i < pages.Length; i++)
            {
                Assert.Equal(before[i] + (ownedPages.Contains(pages[i]) ? 1 : 0), pages[i].ReferenceCount);
            }
        }
        finally
        {
            value.Dispose();
        }

        Assert.Equal(before, pages.Select(page => page.ReferenceCount));
    }

    [Fact]
    public void ArcInput_DeserializesThroughConsecutiveLeadingEmptyPages()
    {
        using var services = Services();
        var serializer = services.GetRequiredService<Serializer<ArcBuffer>>();
        using var source = new ArcBufferWriter();
        var expected = Bytes(50037);
        source.Write(expected);
        using var value = source.PeekSlice(source.Length);
        using var wire = new ArcBufferWriter();
        wire.GetSpan(ArcBufferWriter.MinimumPageSize * 2);
        wire.GetSpan(ArcBufferWriter.MinimumPageSize * 4);
        wire.Write(serializer.SerializeToArray(value));
        using var input = wire.PeekSlice(wire.Length);
        var pages = input.Pages.ToArray();
        Assert.Equal(0, pages[0].Length);
        Assert.Equal(0, pages[1].Length);
        using var decoded = serializer.Deserialize(input);
        Assert.Equal(expected, decoded.ToArray());
        Assert.Same(pages[2], decoded.First);
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

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(50037)]
    public void ReadOwnedBuffer_NonArcInputsReturnOwnedBytesAndAdvanceExactly(int length)
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var input = Bytes(length + 11);
        var expected = input.AsSpan(7, length).ToArray();
        var spanReader = Reader.Create(input, session);
        spanReader.Skip(7);
        var spanValue = spanReader.ReadOwnedBuffer(length);
        Assert.Equal(length + 7, spanReader.Position);
        Assert.Equal(4, spanReader.Remaining);

        using var pooled = new PooledBuffer();
        pooled.Write(input);
        var sequenceReader = Reader.Create(pooled.AsReadOnlySequence(), session);
        sequenceReader.Skip(7);
        var sequenceValue = sequenceReader.ReadOwnedBuffer(length);
        Assert.Equal(length + 7, sequenceReader.Position);
        Assert.Equal(4, sequenceReader.Remaining);

        var pooledReader = Reader.Create(pooled, session);
        pooledReader.Skip(7);
        var pooledValue = pooledReader.ReadOwnedBuffer(length);
        Assert.Equal(length + 7, pooledReader.Position);
        Assert.Equal(4, pooledReader.Remaining);

        using var stream = new MemoryStream(input, writable: false);
        var streamReader = Reader.Create(stream, session);
        streamReader.Skip(7);
        var streamValue = streamReader.ReadOwnedBuffer(length);
        Assert.Equal(length + 7, streamReader.Position);
        Assert.Equal(4, streamReader.Remaining);

        pooled.Dispose();
        stream.Dispose();
        Array.Fill(input, (byte)0);
        var values = new[] { spanValue, sequenceValue, pooledValue, streamValue };
        foreach (var buffer in values)
        {
            var owned = buffer;
            Assert.Equal(expected, owned.ToArray());
            if (length == 0)
            {
                Assert.Null(owned.First);
                owned.Dispose();
            }
            else
            {
                var pages = owned.Pages.ToArray();
                Assert.All(pages, page => Assert.Equal(1, page.ReferenceCount));
                owned.Dispose();
                Assert.All(pages, page => Assert.Equal(0, page.ReferenceCount));
            }
        }
    }

    [Fact]
    public void ReadOwnedBuffer_CopyFillsPagesBeforeAllocatingAnother()
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var expected = Bytes(ArcBufferWriter.MinimumPageSize * 3);
        var reader = Reader.Create(expected, session);
        using var value = reader.ReadOwnedBuffer(expected.Length);
        var pages = value.Pages.ToArray();
        Assert.Equal(3, pages.Length);
        Assert.All(pages, page => Assert.Equal(ArcBufferWriter.MinimumPageSize, page.Length));
        Assert.All(pages, page => Assert.Equal(1, page.ReferenceCount));
        Assert.Equal(expected, value.ToArray());
        Assert.Equal(expected.Length, reader.Position);
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(int.MaxValue)]
    public void ReadOwnedBuffer_InvalidLengthsPreservePositionAndOwnership(int length)
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        using var source = new ArcBufferWriter();
        source.Write(Bytes(7));
        using var input = source.PeekSlice(source.Length);
        var firstPage = Assert.IsType<ArcBufferPage>(input.First);
        var before = firstPage.ReferenceCount;
        var arcReader = Reader.Create(input, session);
        VerifyInvalidLength(ref arcReader, length);
        Assert.Equal(before, firstPage.ReferenceCount);

        var spanReader = Reader.Create(Bytes(7), session);
        VerifyInvalidLength(ref spanReader, length);
        var sequenceReader = Reader.Create(new ReadOnlySequence<byte>(Bytes(7)), session);
        VerifyInvalidLength(ref sequenceReader, length);
        using var stream = new MemoryStream(Bytes(7), writable: false);
        var streamReader = Reader.Create(stream, session);
        VerifyInvalidLength(ref streamReader, length);
        using var pooled = new PooledBuffer();
        pooled.Write(Bytes(7));
        var pooledReader = Reader.Create(pooled, session);
        VerifyInvalidLength(ref pooledReader, length);
    }

    private static void VerifyInvalidLength<TInput>(ref Reader<TInput> reader, int length)
    {
        Exception? error = null;
        try
        {
            using var value = reader.ReadOwnedBuffer(length);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        Assert.Equal(length < 0 ? typeof(ArgumentOutOfRangeException) : typeof(IndexOutOfRangeException), error?.GetType());
        Assert.Equal(0, reader.Position);
        Assert.Equal(7, reader.Remaining);
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
        Assert.Equal(2, Assert.IsType<ArcBufferPage>(value.First).ReferenceCount);
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
    public void EmptyArcInputPayload_ReturnsOwnerFreeBuffer(bool pageBacked)
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        if (!pageBacked)
        {
            var reader = Reader.Create(ArcBuffer.Empty, session);
            var empty = reader.ReadOwnedBuffer(0);
            Assert.True(empty.IsEmpty);
            Assert.Null(empty.First);
            Assert.Equal(0, reader.Position);
            empty.Dispose();
            return;
        }

        using var writer = new ArcBufferWriter();
        using var input = writer.PeekSlice(0);
        var firstPage = Assert.IsType<ArcBufferPage>(input.First);
        var before = firstPage.ReferenceCount;
        var ownedReader = Reader.Create(input, session);
        var result = ownedReader.ReadOwnedBuffer(0);
        Assert.True(result.IsEmpty);
        Assert.Null(result.First);
        Assert.Equal(0, ownedReader.Position);
        Assert.Equal(before, firstPage.ReferenceCount);
        result.Dispose();
        Assert.Equal(before, firstPage.ReferenceCount);
    }

    [Fact]
    public void ReadOwnedBuffer_ZeroLengthAtNonzeroOffsetReturnsOwnerFreeBuffer()
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        using var source = new ArcBufferWriter();
        source.Write(Bytes(50037));
        using var input = source.PeekSlice(source.Length);
        var pages = input.Pages.ToArray();
        var before = pages.Select(page => page.ReferenceCount).ToArray();
        var reader = Reader.Create(input, session);
        reader.Skip(17000);
        var remaining = reader.Remaining;
        var empty = reader.ReadOwnedBuffer(0);
        Assert.True(empty.IsEmpty);
        Assert.Null(empty.First);
        Assert.Equal(17000, reader.Position);
        Assert.Equal(remaining, reader.Remaining);
        Assert.Equal(before, pages.Select(page => page.ReferenceCount));
        empty.Dispose();
        Assert.Equal(before, pages.Select(page => page.ReferenceCount));
    }

    [Theory]
    [InlineData(0x80000000U)]
    [InlineData(uint.MaxValue)]
    public void OversizedLength_IsRejectedBeforeReadingOrPinningPayload(uint encodedLength)
    {
        using var services = Services();
        using var wire = new ArcBufferWriter();
        using (var writeSession = services.GetRequiredService<SerializerSessionPool>().GetSession())
        {
            var writer = Writer.Create(wire, writeSession);
            writer.WriteFieldHeader(0, typeof(ArcBuffer), typeof(ArcBuffer), Orleans.Serialization.WireProtocol.WireType.LengthPrefixed);
            writer.WriteVarUInt32(encodedLength);
            writer.Commit();
        }
        using var input = wire.PeekSlice(wire.Length);
        var firstPage = Assert.IsType<ArcBufferPage>(input.First);
        var before = firstPage.ReferenceCount;
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(input, session);
        Exception? error = null;
        try { new ArcBufferCodec().ReadValue(ref reader, reader.ReadFieldHeader()); }
        catch (Exception exception) { error = exception; }
        Assert.IsType<IndexOutOfRangeException>(error);
        Assert.Equal(before, firstPage.ReferenceCount);
    }

    [Fact]
    public void TruncatedLengthPrefix_IsRejectedWithoutPinningPayload()
    {
        using var services = Services();
        using var wire = new ArcBufferWriter();
        using (var writeSession = services.GetRequiredService<SerializerSessionPool>().GetSession())
        {
            var writer = Writer.Create(wire, writeSession);
            writer.WriteFieldHeader(0, typeof(ArcBuffer), typeof(ArcBuffer), Orleans.Serialization.WireProtocol.WireType.LengthPrefixed);
            writer.WriteVarUInt32(50037);
            writer.Commit();
        }

        wire.Truncate(wire.Length - 1);
        using var input = wire.PeekSlice(wire.Length);
        var firstPage = Assert.IsType<ArcBufferPage>(input.First);
        var before = firstPage.ReferenceCount;
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(input, session);
        try
        {
            new ArcBufferCodec().ReadValue(ref reader, reader.ReadFieldHeader());
            Assert.Fail("Expected truncated length prefix");
        }
        catch (InvalidOperationException)
        {
        }

        Assert.Equal(before, firstPage.ReferenceCount);
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
        Assert.Equal(2, Assert.IsType<ArcBufferPage>(value.First).ReferenceCount);
    }

    [Fact]
    public void ReadOwnedBuffer_InputFailurePropagatesAfterPartialCopy()
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var input = new FailingReaderInput(Bytes(50037));
        var reader = new Reader<ReaderInput>(input, session, 0);
        Exception? error = null;
        try
        {
            using var value = reader.ReadOwnedBuffer(50037);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        Assert.IsType<IOException>(error);
        Assert.Equal(2, input.ByteReadCalls);
        Assert.Equal(ArcBufferWriter.MinimumPageSize, reader.Position);
        Assert.Equal(50037 - ArcBufferWriter.MinimumPageSize, reader.Remaining);
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

[GenerateSerializer]
public sealed class ArcContainer
{
    [Id(0)]
    public ArcBuffer Buffer { get; set; }
}
