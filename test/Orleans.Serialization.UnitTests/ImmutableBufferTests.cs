using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class ImmutableBufferTests
{
    [Theory]
    [InlineData("span")]
    [InlineData("sequence")]
    [InlineData("arc")]
    public void CopyingConstructors_OwnExactBytes_AfterInputMutation(string inputKind)
    {
        byte[] source = [0x00, 0xff, 0x80, 0x2d, 0x11, 0x7f, 0xea];
        byte[] expected = [0x00, 0xff, 0x80, 0x2d, 0x11, 0x7f, 0xea];
        ImmutableBuffer snapshot;
        if (inputKind == "arc")
        {
            using var writer = new ArcBufferWriter();
            writer.Write(source);
            using var input = writer.PeekSlice(writer.Length);
            var references = input.First.ReferenceCount;

            snapshot = new ImmutableBuffer(input);

            Assert.Equal(references, input.First.ReferenceCount);
            Assert.Equal(expected, input.ToArray());
            Assert.Equal(expected.Length, writer.Length);
            writer.WriteAt(0, [0x42, 0x43]);
            Assert.Equal(0x42, input.ToArray()[0]);
            Assert.Equal(0x43, writer.Reader.Peek(1));
            writer.Write([0x44]);
            Assert.Equal(expected.Length + 1, writer.Length);
            Assert.Equal(expected.Length, input.Length);
        }
        else
        {
            snapshot = inputKind == "span"
                ? new ImmutableBuffer(source.AsSpan())
                : new ImmutableBuffer(new ReadOnlySequence<byte>(source));
        }

        Array.Fill(source, (byte)0x55);
        AssertBuffer(snapshot, expected);
    }

    [Theory]
    [InlineData("span")]
    [InlineData("sequence")]
    [InlineData("arc")]
    public void CopyingConstructors_EmptyInputs_ExposeEmptyViews(string inputKind)
    {
        using var writer = new ArcBufferWriter();
        using var input = writer.PeekSlice(0);
        var references = input.First.ReferenceCount;
        var snapshot = inputKind switch
        {
            "span" => new ImmutableBuffer(ReadOnlySpan<byte>.Empty),
            "sequence" => new ImmutableBuffer(ReadOnlySequence<byte>.Empty),
            "arc" => new ImmutableBuffer(input),
            _ => throw new ArgumentOutOfRangeException(nameof(inputKind))
        };

        AssertBuffer(snapshot, []);
        AssertBuffer(ImmutableBuffer.Empty, []);
        Assert.Equal(references, input.First.ReferenceCount);
        Assert.Equal(0, writer.Length);
        writer.Write([0x81]);
        Assert.Equal(0x81, writer.Reader.Peek(0));
        AssertBuffer(snapshot, []);
    }

    [Fact]
    public void SequenceConstructor_MultiSegmentSlice_CopiesOnlySelectedBytes()
    {
        byte[] first = [0x10, 0x00, 0xff];
        byte[] middle = [0x80, 0x31, 0x32, 0x7f];
        byte[] last = [0xea, 0x99, 0x20];
        var input = Sequence(first, [], middle, last).Slice(1, 8);
        Assert.False(input.IsSingleSegment);

        var snapshot = new ImmutableBuffer(input);
        Array.Fill(first, (byte)0x41);
        Array.Fill(middle, (byte)0x42);
        Array.Fill(last, (byte)0x43);

        AssertBuffer(snapshot, [0x00, 0xff, 0x80, 0x31, 0x32, 0x7f, 0xea, 0x99]);
        Assert.True(snapshot.AsReadOnlySequence().IsSingleSegment);
    }

    [Fact]
    public void SequenceConstructor_TooLarge_RejectsBeforeCopying()
    {
        // Sparse sequence metadata reaches the length guard without a multi-GB allocation.
        var first = new Segment(new byte[] { 0x31 });
        var last = first.Append(new byte[] { 0x82 }, int.MaxValue);
        var input = new ReadOnlySequence<byte>(first, 0, last, 1);
        Assert.Equal((long)int.MaxValue + 1, input.Length);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ImmutableBuffer(input));

        Assert.Equal("bytes", exception.ParamName);
        Assert.Equal(new byte[] { 0x31 }, first.Memory.ToArray());
        Assert.Equal(new byte[] { 0x82 }, last.Memory.ToArray());
    }

    [Fact]
    public void ArcConstructor_SnapshotSurvivesSliceAndWriterDisposalPoolReuseAndGc()
    {
        var source = RawBytes(ArcBufferWriter.MinimumPageSize * 2 + 73);
        const int offset = 19;
        var length = source.Length - offset - 23;
        var expected = source.AsSpan(offset, length).ToArray();
        ImmutableBuffer snapshot;
        ReadOnlyMemory<byte> memory;
        ReadOnlySequence<byte> sequence;
        List<ArcBufferPage> pages;
        using (var writer = new ArcBufferWriter())
        {
            writer.Write(source);
            using (var all = writer.PeekSlice(writer.Length))
            {
                using var input = all.Slice(offset, length);
                pages = input.Pages.ToList();
                var referenceCounts = pages.Select(page => page.ReferenceCount).ToArray();
                Assert.False(input.AsReadOnlySequence().IsSingleSegment);

                snapshot = new ImmutableBuffer(input);
                memory = snapshot.Memory;
                sequence = snapshot.AsReadOnlySequence();

                Assert.Equal(referenceCounts, pages.Select(page => page.ReferenceCount).ToArray());
                Assert.Equal(expected, input.ToArray());
                Assert.Equal(source.Length, writer.Length);
            }

            using var remaining = writer.PeekSlice(writer.Length);
            Assert.All(remaining.Pages.ToList(), page => Assert.Equal(2, page.ReferenceCount));
        }

        Assert.All(pages, page => Assert.Equal(0, page.ReferenceCount));
        Array.Fill(source, (byte)0x44);
        // Churn the existing pool without changing its process-wide configuration.
        for (var i = 0; i < 300; i++)
        {
            using var writer = new ArcBufferWriter();
            writer.Write(new byte[ArcBufferWriter.MinimumPageSize * 3]);
            writer.WriteAt(0, [0x52, 0x53]);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        AssertBuffer(snapshot, expected);
        Assert.Equal(expected, memory.ToArray());
        Assert.Equal(expected.Length, memory.Length);
        Assert.Equal(expected, sequence.ToArray());
        Assert.Equal(expected.Length, sequence.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ArcBufferWriter.MinimumPageSize + 37)]
    public void AsReadOnlySequence_ExposesExactFrozenBytes(int length)
    {
        var source = RawBytes(length);
        var expected = RawBytes(length);
        var first = source[..(length / 2)];
        var last = source[(length / 2)..];
        var snapshot = new ImmutableBuffer(Sequence(first, [], last));
        Array.Fill(first, (byte)0x33);
        Array.Fill(last, (byte)0x34);

        var sequence = snapshot.AsReadOnlySequence();

        Assert.Equal(expected, sequence.ToArray());
        Assert.Equal(length, sequence.Length);
        Assert.Equal(expected, snapshot.Memory.ToArray());
        Assert.True(sequence.IsSingleSegment);
        Assert.Equal(snapshot.Memory, sequence.First);
    }

    [Fact]
    public void Create_CallbackRunsOnce_UsesOnlyAdvancedBytes()
    {
        var calls = 0;
        var snapshot = ImmutableBuffer.Create(writer =>
        {
            calls++;
            var span = writer.GetSpan(8);
            new byte[] { 0x00, 0xff, 0x80, 0x31, 0xea, 0x99, 0x51, 0x52 }.CopyTo(span);
            writer.Advance(3);
            var tail = writer.GetSpan(2);
            tail[0] = 0x7f;
            tail[1] = 0x88;
            writer.Advance(1);
        });

        Assert.Equal(1, calls);
        AssertBuffer(snapshot, [0x00, 0xff, 0x80, 0x7f]);
    }

    [Fact]
    public void Create_NoWrites_ReturnsEmptyBytes()
    {
        var calls = 0;
        var snapshot = ImmutableBuffer.Create(writer =>
        {
            calls++;
            writer.GetSpan(4).Fill(0x7b);
        });

        Assert.Equal(1, calls);
        AssertBuffer(snapshot, []);
    }

    [Fact]
    public void Create_NullCallback_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => ImmutableBuffer.Create(null!));

        Assert.Equal("write", exception.ParamName);
        AssertBuffer(ImmutableBuffer.Create(writer => writer.Write(new byte[] { 0x81, 0x22 })), [0x81, 0x22]);
    }

    [Fact]
    public void Create_CallbackFailure_PropagatesSameException()
    {
        var sentinel = new InvalidOperationException("callback sentinel");
        var calls = 0;
        ImmutableBuffer? result = null;

        var exception = Assert.Throws<InvalidOperationException>(() => result = ImmutableBuffer.Create(writer =>
        {
            calls++;
            writer.Write(new byte[] { 0x00, 0xff, 0x80 });
            throw sentinel;
        }));

        Assert.Same(sentinel, exception);
        Assert.Equal(1, calls);
        Assert.Null(result);
    }

    [Fact]
    public void Create_RetainedWriterMemoryCannotMutateSnapshot()
    {
        IBufferWriter<byte> retainedWriter = null!;
        Memory<byte> retainedMemory = default;
        var snapshot = ImmutableBuffer.Create(writer =>
        {
            retainedWriter = writer;
            retainedMemory = writer.GetMemory(8);
            new byte[] { 0x00, 0xff, 0x80, 0x11 }.CopyTo(retainedMemory.Span);
            writer.Advance(4);
        });

        retainedMemory.Span.Fill(0x42);
        retainedWriter.Write(new byte[] { 0x52, 0x53, 0x54 });

        AssertBuffer(snapshot, [0x00, 0xff, 0x80, 0x11]);
        Assert.True(MemoryMarshal.TryGetArray(snapshot.Memory, out var frozen));
        Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)retainedMemory, out var temporary));
        Assert.NotSame(temporary.Array, frozen.Array);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("span")]
    [InlineData("sequence")]
    [InlineData("arc")]
    public void Serializer_RoundTripsRawBytes_WithoutApplicationCodecs(string inputKind)
    {
        var source = RawBytes(inputKind == "empty" ? 0 : ArcBufferWriter.MinimumPageSize + 37);
        var expected = source.ToArray();
        var snapshot = BorrowedSnapshot(inputKind, source);
        Array.Fill(source, (byte)0x41);
        using var sendingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receivingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();

        var wireBytes = sendingServices.GetRequiredService<Serializer<ImmutableBuffer>>().SerializeToArray(snapshot);
        var decoded = receivingServices.GetRequiredService<Serializer<ImmutableBuffer>>().Deserialize(wireBytes);

        AssertBuffer(decoded!, expected);
        AssertBuffer(snapshot, expected);
        Assert.NotSame(snapshot, decoded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepCopy_SharesImmutableBufferAndPreservesBytes(bool empty)
    {
        var source = RawBytes(empty ? 0 : 257);
        var expected = source.ToArray();
        var snapshot = empty ? ImmutableBuffer.Empty : new ImmutableBuffer(source.AsSpan());
        Array.Fill(source, (byte)0x42);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();

        var copy = services.GetRequiredService<DeepCopier>().Copy(snapshot);
        GC.Collect();

        Assert.Same(snapshot, copy);
        AssertBuffer(copy!, expected);
        Assert.Single(typeof(ImmutableBuffer).GetCustomAttributes(typeof(ImmutableAttribute), false));
        Assert.Single(typeof(ImmutableBuffer).GetCustomAttributes(typeof(GenerateSerializerAttribute), false));
    }

    [Fact]
    public void Serializer_RepeatedBufferReferences_PreserveGraphSharing()
    {
        byte[] source = [0x00, 0xff, 0x80, 0x11, 0xea];
        var snapshot = new ImmutableBuffer(source.AsSpan());
        Array.Fill(source, (byte)0x42);
        using var sendingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receivingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var wireBytes = sendingServices.GetRequiredService<Serializer<ImmutableBuffer[]>>()
            .SerializeToArray([snapshot, snapshot]);

        var decoded = receivingServices.GetRequiredService<Serializer<ImmutableBuffer[]>>().Deserialize(wireBytes)!;

        Assert.Equal(2, decoded.Length);
        Assert.Same(decoded[0], decoded[1]);
        Assert.NotSame(snapshot, decoded[0]);
        AssertBuffer(decoded[0], [0x00, 0xff, 0x80, 0x11, 0xea]);
    }

    private static void AssertBuffer(ImmutableBuffer buffer, byte[] expected)
    {
        Assert.Equal(expected.Length, buffer.Length);
        Assert.Equal(expected, buffer.Memory.ToArray());
        Assert.Equal(expected.Length, buffer.Memory.Length);
        var sequence = buffer.AsReadOnlySequence();
        Assert.Equal(expected.Length, sequence.Length);
        Assert.Equal(expected, sequence.ToArray());
    }

    private static ImmutableBuffer BorrowedSnapshot(string inputKind, byte[] source)
    {
        switch (inputKind)
        {
            case "empty":
                return new ImmutableBuffer(ReadOnlySpan<byte>.Empty);
            case "span":
                return new ImmutableBuffer(source.AsSpan());
            case "sequence":
            {
                var first = source[..17];
                var middle = source[17..^13];
                var last = source[^13..];
                var snapshot = new ImmutableBuffer(Sequence(first, [], middle, last));
                Array.Fill(first, (byte)0x41);
                Array.Fill(middle, (byte)0x42);
                Array.Fill(last, (byte)0x43);
                return snapshot;
            }
            case "arc":
                using (var writer = new ArcBufferWriter())
                {
                    writer.Write([0x51, 0x52, 0x53, 0x54, 0x55]);
                    writer.Write(source);
                    writer.Write([0x61, 0x62, 0x63]);
                    using var all = writer.PeekSlice(writer.Length);
                    using var input = all.Slice(5, source.Length);
                    Assert.False(input.AsReadOnlySequence().IsSingleSegment);
                    return new ImmutableBuffer(input);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(inputKind));
        }
    }

    private static byte[] RawBytes(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)((i * 73 + (i % 3 == 0 ? 0 : 0x80)) & 0xff)).ToArray();

    private static ReadOnlySequence<byte> Sequence(params byte[][] segments)
    {
        var first = new Segment(segments[0]);
        var last = first;
        foreach (var bytes in segments.Skip(1))
        {
            last = last.Append(bytes);
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory, long? runningIndex = null)
        {
            var next = new Segment(memory)
            {
                RunningIndex = runningIndex ?? RunningIndex + Memory.Length
            };
            Next = next;
            return next;
        }
    }
}
