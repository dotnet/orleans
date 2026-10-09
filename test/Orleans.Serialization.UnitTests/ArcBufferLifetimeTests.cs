using System;
using System.Buffers;
using System.Linq;
using Orleans.Serialization.Buffers;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public void ZeroLengthPageBackedSlice_OwnsIndependentPin()
    {
        var writer = new ArcBufferWriter();
        var empty = writer.ConsumeSlice(0);
        var page = empty.First;
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
        using var services = ArcBufferCodecTests.Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = Reader.Create(value, session);
            _ = reader.Length;
        });
        Assert.Equal(new byte[] { 0x80, 0xff }, retained.ToArray());
    }
}
