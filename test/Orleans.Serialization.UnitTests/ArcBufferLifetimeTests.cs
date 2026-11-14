using System;
using System.Buffers;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class ArcBufferLifetimeTests
{
    [Fact]
    public void DefaultAndEmpty_AreOwnerFreeAndReadable()
    {
        ArcBuffer value = default;
        Assert.True(value.IsEmpty);
        Assert.Null(value.First);
        Assert.Empty(value.ToArray());
        Assert.True(value.AsReadOnlySequence().IsEmpty);
        Assert.Equal(0, value.CopyTo(Span<byte>.Empty));
        Assert.False(value.MemorySegments.MoveNext());
        using var retained = value.Slice(0);
        Assert.Null(retained.First);
        value.Pin();
        value.Dispose();
        value.Dispose();
        Assert.Empty(ArcBuffer.Empty.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => value.Slice(1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public void OwnerFreeBuffer_RejectsInvalidShape(int offset, int length)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ArcBuffer(null, 0, offset, length));
        Assert.Equal(offset == 0 ? "length" : "offset", error.ParamName);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(ArcBufferWriter.MinimumPageSize + 1, 0)]
    [InlineData(0, ArcBufferWriter.MinimumPageSize * 3 + 18)]
    [InlineData(16, ArcBufferWriter.MinimumPageSize * 3 + 2)]
    [InlineData(0, int.MaxValue)]
    public void PublicConstructor_RejectsInvalidPageBoundsWithoutAcquiringPins(int offset, int length)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(Bytes(ArcBufferWriter.MinimumPageSize * 3 + 17));
        using var input = writer.PeekSlice(writer.Length);
        var first = Assert.IsType<ArcBufferPage>(input.First);
        var pages = input.Pages.ToArray();
        var before = pages.Select(page => page.ReferenceCount).ToArray();
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ArcBuffer(first, first.Version, offset, length));
        Assert.Equal(offset < 0 || offset > first.Length ? "offset" : "length", error.ParamName);
        Assert.Equal(before, pages.Select(page => page.ReferenceCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(ArcBufferWriter.MinimumPageSize)]
    public void PublicConstructor_BorrowsValidMultiPageBytes(int offset)
    {
        using var writer = new ArcBufferWriter();
        var expected = Bytes(ArcBufferWriter.MinimumPageSize * 3 + 17);
        writer.Write(expected);
        using var input = writer.PeekSlice(writer.Length);
        var first = Assert.IsType<ArcBufferPage>(input.First);
        var pages = input.Pages.ToArray();
        var before = pages.Select(page => page.ReferenceCount).ToArray();
        var borrowed = new ArcBuffer(first, first.Version, offset, expected.Length - offset);
        Assert.Equal(expected.AsSpan(offset).ToArray(), borrowed.ToArray());
        Assert.Equal(before, pages.Select(page => page.ReferenceCount));
        borrowed.Pin();
        borrowed.Dispose();
        Assert.Equal(before, pages.Select(page => page.ReferenceCount));
    }

    [Fact]
    public void PublicConstructor_RejectsInvalidPageToken()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 1 });
        using var input = writer.PeekSlice(1);
        var first = Assert.IsType<ArcBufferPage>(input.First);
        var before = first.ReferenceCount;
        Assert.Throws<InvalidOperationException>(() => new ArcBuffer(first, first.Version + 1, 0, 1));
        Assert.Equal(before, first.ReferenceCount);
    }

    [Fact]
    public void PublicEmptyPageContract_IsNullable()
    {
        var nullability = new NullabilityInfoContext();
        var field = typeof(ArcBuffer).GetField(nameof(ArcBuffer.First))!;
        var constructor = typeof(ArcBuffer).GetConstructors().Single();
        Assert.Equal(NullabilityState.Nullable, nullability.Create(field).ReadState);
        Assert.Equal(NullabilityState.Nullable, nullability.Create(constructor.GetParameters()[0]).ReadState);
        var value = new ArcBuffer(null, 0, 0, 0);
        Assert.Null(value.First);
        Assert.Empty(value.ToArray());
        value.Dispose();
    }

    [Theory]
    [InlineData(ArcBufferWriter.MinimumPageSize)]
    [InlineData(ArcBufferWriter.MinimumPageSize + 1)]
    [InlineData(ArcBufferWriter.MinimumPageSize * 3 + 17)]
    public void ZeroLengthSlice_KeepsOffsetWithinReferencedPage(int offset)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(Bytes(ArcBufferWriter.MinimumPageSize * 3 + 17));
        using var input = writer.PeekSlice(writer.Length);
        using var empty = input.Slice(offset, 0);
        var page = Assert.IsType<ArcBufferPage>(empty.First);
        Assert.InRange(empty.Offset, 0, page.Length);
        Assert.Empty(empty.ToArray());
        var borrowed = new ArcBuffer(page, page.Version, empty.Offset, empty.Length);
        Assert.Empty(borrowed.ToArray());
        Assert.Equal(empty.Offset, borrowed.Offset);
    }

    [Fact]
    public void ZeroLengthPageBackedSlice_OwnsIndependentPin()
    {
        var writer = new ArcBufferWriter();
        var empty = writer.ConsumeSlice(0);
        var page = Assert.IsType<ArcBufferPage>(empty.First);
        Assert.Equal(2, page.ReferenceCount);
        var retained = empty.Slice(0);
        Assert.Equal(3, page.ReferenceCount);
        writer.Dispose();
        Assert.Equal(2, page.ReferenceCount);
        empty.Dispose();
        Assert.Throws<InvalidOperationException>(() => empty.Dispose());
        Assert.Equal(1, page.ReferenceCount);
        Assert.Throws<InvalidOperationException>(() => empty.ToArray());
        Assert.Throws<InvalidOperationException>(() => empty.AsReadOnlySequence());
        Assert.Empty(retained.ToArray());
        retained.Dispose();
        Assert.Equal(0, page.ReferenceCount);
    }

    [Fact]
    public void RetainedMultiPageSlice_SurvivesWriterDisposalAndPoolReuse()
    {
        var expected = Enumerable.Range(0, ArcBufferWriter.MinimumPageSize * 3 + 91).Select(i => (byte)i).ToArray();
        var writer = new ArcBufferWriter();
        writer.Write(expected);
        var original = writer.ConsumeSlice(writer.Length);
        var pages = original.Pages.ToArray();
        Assert.True(pages.Length > 1);
        using var retained = original.Slice(19, expected.Length - 47);
        writer.Dispose();
        original.Dispose();
        Assert.All(pages, page => Assert.Equal(1, page.ReferenceCount));
        for (var i = 0; i < 8; i++)
        {
            using var reuse = new ArcBufferWriter();
            reuse.Write(new byte[ArcBufferWriter.MinimumPageSize * 4]);
        }

        Assert.Equal(expected.AsSpan(19, expected.Length - 47).ToArray(), retained.ToArray());
    }

    [Fact]
    public void DisposedBuffer_RejectsReadersAndEnumerationEvenWhileAnotherPinExists()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 0x80, 0xff });
        var value = writer.PeekSlice(2);
        using var retained = value.Slice(0);
        value.Dispose();
        Assert.Throws<InvalidOperationException>(() => value.ToArray());
        Assert.Throws<InvalidOperationException>(() => value.AsReadOnlySequence());
        Assert.Throws<InvalidOperationException>(() => value.Slice(0));
        Assert.Throws<InvalidOperationException>(() => value.MemorySegments.MoveNext());
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = Reader.Create(value, session);
            _ = reader.Length;
        });
        Assert.Equal(new byte[] { 0x80, 0xff }, retained.ToArray());
    }

    [Fact]
    public void DisposedWriter_RejectsMutationAndCanBeReset()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 1, 2, 3 });
        using var retained = writer.PeekSlice(writer.Length);
        writer.Dispose();
        writer.Dispose();
        Assert.Equal(new byte[] { 1, 2, 3 }, retained.ToArray());
        Assert.Throws<ObjectDisposedException>(() => writer.GetMemory());
        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan());
        Assert.Throws<ObjectDisposedException>(() => writer.AdvanceWriter(1));
        Assert.Throws<ObjectDisposedException>(() => writer.Write(new byte[] { 4 }));
        Assert.Throws<ObjectDisposedException>(() => writer.WriteAt(0, new byte[] { 4 }));
        Assert.Throws<ObjectDisposedException>(() => writer.Truncate(0));
        writer.Reset();
        writer.Write(new byte[] { 5, 6 });
        using var resetSlice = writer.PeekSlice(writer.Length);
        Assert.Equal(new byte[] { 5, 6 }, resetSlice.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, retained.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppendPinned_EmptyInputChecksWriterDisposalWithoutAcquiringPins(bool pageBacked)
    {
        using var source = new ArcBufferWriter();
        using var input = pageBacked ? source.PeekSlice(0) : ArcBuffer.Empty;
        var page = input.First;
        var references = page?.ReferenceCount;
        using var writer = new ArcBufferWriter();
        writer.AppendPinned(input);
        Assert.Equal(0, writer.Length);
        Assert.Equal(references, page?.ReferenceCount);
        writer.Write(new byte[] { 1, 2 });
        writer.AppendPinned(input);
        using var retained = writer.PeekSlice(writer.Length);
        Assert.Equal(new byte[] { 1, 2 }, retained.ToArray());
        Assert.Equal(references, page?.ReferenceCount);

        writer.Dispose();
        var error = Assert.Throws<ObjectDisposedException>(() => writer.AppendPinned(input));
        Assert.Equal(nameof(ArcBufferWriter), error.ObjectName);
        Assert.Equal(0, writer.Length);
        Assert.Equal(references, page?.ReferenceCount);
        Assert.Equal(new byte[] { 1, 2 }, retained.ToArray());

        writer.Reset();
        writer.AppendPinned(input);
        writer.Write(new byte[] { 3 });
        using var resetSlice = writer.PeekSlice(writer.Length);
        Assert.Equal(new byte[] { 3 }, resetSlice.ToArray());
        Assert.Equal(references, page?.ReferenceCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void DisposedWriter_RejectsReaderCursorMutationsAndCanBeReset(int count)
    {
        using var writer = new ArcBufferWriter();
        var reader = writer.Reader;
        writer.Write(new byte[] { 1, 2, 3 });
        using var retained = writer.PeekSlice(writer.Length);
        writer.Dispose();

        var error = Assert.Throws<ObjectDisposedException>(() => writer.AdvanceReader(count));
        Assert.Equal(nameof(ArcBufferWriter), error.ObjectName);
        Assert.Throws<ObjectDisposedException>(() => reader.Skip(count));
        Assert.Throws<ObjectDisposedException>(() => writer.PeekSlice(count));
        Assert.Throws<ObjectDisposedException>(() => writer.ConsumeSlice(count));
        Assert.Throws<ObjectDisposedException>(() => reader.PeekSlice(count));
        Assert.Throws<ObjectDisposedException>(() => reader.ConsumeSlice(count));
        if (count == 0)
        {
            Assert.Throws<ObjectDisposedException>(() => reader.Consume(Span<byte>.Empty));
            Assert.Throws<ObjectDisposedException>(() => reader.IsNext(ReadOnlySpan<byte>.Empty, advancePast: true));
        }

        Assert.Equal(0, writer.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, retained.ToArray());
        writer.Reset();
        writer.Write(new byte[] { 5, 6 });
        writer.AdvanceReader(0);
        reader.Skip(1);
        Assert.Equal(1, reader.Length);
        using var remainder = reader.ConsumeSlice(1);
        Assert.Equal(new byte[] { 6 }, remainder.ToArray());
        Assert.Equal(0, reader.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, retained.ToArray());
    }

    [Fact]
    public void PinnedWriter_AdvancesReaderAcrossPagesAndCanBeReset()
    {
        using var source = new ArcBufferWriter();
        var expected = Bytes(ArcBufferWriter.MinimumPageSize + 3);
        source.Write(expected);
        using var input = source.PeekSlice(source.Length);
        var pages = input.Pages.ToArray();
        Assert.Equal(2, pages.Length);
        using var writer = new ArcBufferWriter();
        writer.AppendPinned(input);
        Assert.All(pages, page => Assert.Equal(3, page.ReferenceCount));

        writer.AppendPinned(ArcBuffer.Empty);
        Assert.Equal(expected.Length, writer.Length);
        Assert.All(pages, page => Assert.Equal(3, page.ReferenceCount));
        writer.AdvanceReader(0);
        Assert.All(pages, page => Assert.Equal(3, page.ReferenceCount));
        var reader = writer.Reader;
        reader.Skip(ArcBufferWriter.MinimumPageSize + 1);
        Assert.Equal(2, reader.Length);
        Assert.Equal(2, pages[0].ReferenceCount);
        Assert.Equal(3, pages[1].ReferenceCount);
        using var remainder = reader.ConsumeSlice(2);
        Assert.Equal(expected.AsSpan(expected.Length - 2).ToArray(), remainder.ToArray());
        Assert.Equal(0, reader.Length);
        Assert.Equal(4, pages[1].ReferenceCount);

        writer.Reset();
        Assert.Equal(2, pages[0].ReferenceCount);
        Assert.Equal(3, pages[1].ReferenceCount);
        writer.Write(new byte[] { 7 });
        reader.Skip(1);
        Assert.Equal(0, reader.Length);
        Assert.Equal(expected, input.ToArray());
        Assert.Equal(expected.AsSpan(expected.Length - 2).ToArray(), remainder.ToArray());
    }

    [Fact]
    public void LeadingEmptyPage_LastOwnerReleaseReturnsEveryPage()
    {
        using var writer = new ArcBufferWriter();
        var length = ArcBufferWriter.MinimumPageSize * 2;
        writer.GetSpan(length)[..length].Fill(0x5a);
        writer.AdvanceWriter(length);
        var original = writer.ConsumeSlice(length);
        var prefix = Assert.IsType<ArcBufferPage>(original.First);
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
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ArcReader_SkipsConsecutiveEmptyPagesForByteAndPrimitiveReads(bool leadingEmptyPages, bool middleEmptyPages)
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        using var source = new ArcBufferWriter();
        if (leadingEmptyPages)
        {
            source.GetSpan(ArcBufferWriter.MinimumPageSize * 2);
            source.GetSpan(ArcBufferWriter.MinimumPageSize * 4);
        }

        source.Write(new byte[] { 0x11, 0x12 });
        if (middleEmptyPages)
        {
            source.GetSpan(ArcBufferWriter.MinimumPageSize * 8);
            source.GetSpan(ArcBufferWriter.MinimumPageSize * 16);
            source.GetSpan(ArcBufferWriter.MinimumPageSize * 32);
        }

        source.Write(new byte[] { 0x21, 0x22, 0x23, 0x24, 0x25 });
        using var input = source.PeekSlice(source.Length);
        var pages = input.Pages.ToArray();
        Assert.True(pages.Count(page => page.Length == 0) >= 2);
        var before = pages.Select(page => page.ReferenceCount).ToArray();
        var reader = Reader.Create(input, session);
        Assert.Equal(0x11, reader.ReadByte());
        Assert.Equal(0x12, reader.ReadByte());
        Assert.Equal(0x24232221U, reader.ReadUInt32());
        Assert.Equal(0x25, reader.ReadByte());
        Assert.Equal(input.Length, reader.Position);
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(before, pages.Select(page => page.ReferenceCount));
    }

    [Fact]
    public void ArcReader_NestedForksPreserveGlobalOffsetsAndBorrowedPins()
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        using var source = new ArcBufferWriter();
        var expected = Bytes(50037);
        source.Write(expected);
        using var input = source.PeekSlice(source.Length);
        var pages = input.Pages.ToArray();
        var before = pages.Select(page => page.ReferenceCount).ToArray();
        var reader = new Reader<ArcBufferReaderInput>(new ArcBufferReaderInput(in input), session, 113);
        reader.Skip(17000);
        Assert.Equal(17113, reader.Position);
        Assert.Equal(expected[17000], reader.ReadByte());
        reader.ForkFrom(18113, out var fork);
        Assert.Equal(18113, fork.Position);
        Assert.Equal(expected.Length - 18000, fork.Remaining);
        Assert.Equal(expected[18000], fork.ReadByte());
        fork.ForkFrom(19113, out var nestedFork);
        Assert.Equal(19113, nestedFork.Position);
        var bytes = new byte[7];
        nestedFork.ReadBytes(bytes);
        Assert.Equal(expected.AsSpan(19000, 7).ToArray(), bytes);
        Assert.Equal(19120, nestedFork.Position);
        Assert.Equal(18114, fork.Position);
        Assert.Equal(17114, reader.Position);
        Assert.Equal(before, pages.Select(page => page.ReferenceCount));
    }

    [Fact]
    public void ArcReader_DefaultInputIsEmptyAndBorrowed()
    {
        using var services = Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(default(ArcBuffer), session);
        Assert.Equal(0, reader.Length);
        Assert.Equal(0, reader.Position);
        Assert.Equal(0, reader.Remaining);
        reader.ReadBytes(Span<byte>.Empty);
        reader.Skip(0);
        reader.ForkFrom(0, out var fork);
        Assert.Equal(0, fork.Length);
        Assert.Equal(0, fork.Position);
        Assert.Equal(0, fork.Remaining);
        try
        {
            reader.ReadByte();
            Assert.Fail("Reading past empty Arc input must throw.");
        }
        catch (IndexOutOfRangeException)
        {
        }
    }

    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 31)).ToArray();
    private static ServiceProvider Services() => new ServiceCollection().AddSerializer().BuildServiceProvider();
}
