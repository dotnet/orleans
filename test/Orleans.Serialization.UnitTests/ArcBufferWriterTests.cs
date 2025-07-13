using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Serialization.UnitTests;

/// <summary>
/// Tests for the ArcBufferWriter class, which provides a high-performance buffer writer implementation
/// for Orleans' serialization system. ArcBufferWriter is a specialized buffer writer that manages
/// memory in pages and supports atomic reference counting (ARC) for efficient memory management.
/// 
/// Orleans' serialization approach emphasizes:
/// - Zero-copy operations where possible through buffer slicing
/// - Efficient memory pooling to reduce GC pressure
/// - Reference counting for safe concurrent access
/// - Support for streaming scenarios with incremental writes and reads
/// </summary>
[Trait("Category", "BVT")]
public class ArcBufferWriterTests
{
    private const int PageSize = ArcBufferWriter.MinimumPageSize;
#if NET6_0_OR_GREATER
    private readonly Random _random = Random.Shared;
#else
    private readonly Random _random = new Random();
#endif

    /// <summary>
    /// Verifies that writing data larger than a single page results in correct multi-page buffer management and correct data retrieval.
    /// </summary>
    [Fact]
    public void MultiPageBuffer_CorrectlyHandlesLargeWritesAndRetrieval()
    {
        using var bufferWriter = new ArcBufferWriter();
        var randomData = new byte[PageSize * 3];
        _random.NextBytes(randomData);
        int[] writeSizes = [1, 52, 125, 4096];
        var i = 0;
        while (bufferWriter.Length < randomData.Length)
        {
            var writeSize = Math.Min(randomData.Length - bufferWriter.Length, writeSizes[i++ % writeSizes.Length]);
            bufferWriter.Write(randomData);
        }

        {
            using var wholeBuffer = bufferWriter.PeekSlice(randomData.Length);
            Assert.Equal(3, wholeBuffer.Pages.Count());
            Assert.Equal(3, wholeBuffer.PageSegments.Count());
            Assert.Equal(3, wholeBuffer.MemorySegments.Count());
            Assert.Equal(3, wholeBuffer.ArraySegments.Count());
            Assert.Equal(randomData, wholeBuffer.AsReadOnlySequence().ToArray());

            {
                using var newWriter = new ArcBufferWriter();
                newWriter.Write(wholeBuffer.AsReadOnlySequence());

                Span<byte> headerBytes = stackalloc byte[8];
                var result = newWriter.Peek(in headerBytes);
                Assert.True(result.Length >= headerBytes.Length);
                Assert.Equal(randomData[0..headerBytes.Length], result[..headerBytes.Length].ToArray());
                var copiedData = new byte[newWriter.Length];
                newWriter.Peek(copiedData);
                newWriter.AdvanceReader(copiedData.Length);
                Assert.Equal(0, newWriter.Length);
                Assert.Equal(randomData, copiedData);
            }

            var spanCount = 0;
            foreach (var span in wholeBuffer.SpanSegments)
            {
                Assert.Equal(PageSize, span.Length);
                var spanArray = span.ToArray();
                Assert.Equal(spanArray, wholeBuffer.ArraySegments.Skip(spanCount).Take(1).Single().ToArray());
                Assert.Equal(spanArray, wholeBuffer.MemorySegments.Skip(spanCount).Take(1).Single().ToArray());
                Assert.Equal(spanArray, wholeBuffer.PageSegments.Skip(spanCount).Take(1).Single().Span.ToArray());
                Assert.Equal(spanArray, wholeBuffer.PageSegments.Skip(spanCount).Take(1).Single().Memory.ToArray());
                Assert.Equal(spanArray, wholeBuffer.PageSegments.Skip(spanCount).Take(1).Single().ArraySegment.ToArray());
                Assert.Equal(spanArray, wholeBuffer.AsReadOnlySequence().Slice(spanCount * PageSize, PageSize).ToArray());
                ++spanCount;
            }

            Assert.Equal(3, spanCount);
        }

        Assert.Equal(randomData.Length, bufferWriter.Length);

        {
            using var peeked = bufferWriter.PeekSlice(3000);
            using var slice = bufferWriter.ConsumeSlice(3000);
            var sliceArray = slice.ToArray();
            Assert.Equal(randomData.AsSpan(0, 3000).ToArray(), sliceArray);
            Assert.Equal(sliceArray, peeked.ToArray());
            Assert.Equal(sliceArray, peeked.AsReadOnlySequence().ToArray());

            Assert.Equal(randomData.Length - sliceArray.Length, bufferWriter.Length);
        }

        {
            using var peeked = bufferWriter.PeekSlice(3000);
            using var slice = bufferWriter.ConsumeSlice(3000);
            var sliceArray = slice.ToArray();
            Assert.Equal(randomData.AsSpan(3000, 3000).ToArray(), sliceArray);
            Assert.Equal(sliceArray, peeked.ToArray());
            Assert.Equal(sliceArray, slice.AsReadOnlySequence().ToArray());

            Assert.Equal(randomData.Length - sliceArray.Length * 2, bufferWriter.Length);
        }

        Assert.Equal(randomData.Length - 6000, bufferWriter.Length);
    }

    /// <summary>
    /// Verifies that page reference counts and versions are managed correctly as slices are consumed and disposed.
    /// </summary>
    [Fact]
    public void PageBufferManagement_TracksReferenceCountsAndVersions()
    {
        var bufferWriter = new ArcBufferWriter();
        var randomData = new byte[PageSize * 12];
        _random.NextBytes(randomData);
        bufferWriter.Write(randomData);

        var peeked = bufferWriter.PeekSlice(randomData.Length);
        var pages = peeked.Pages.ToList();
        peeked.Dispose();

        var expected = pages.Select((p, i) => (p.Version, p.ReferenceCount)).ToList();
        CheckPages(pages, expected);

        var slice = bufferWriter.ConsumeSlice(PageSize - 1);
        slice.Dispose();

        CheckPages(pages, expected);

        slice = bufferWriter.ConsumeSlice(1);
        CheckPages(pages, expected);
        slice.Dispose();

        expected[0] = (expected[0].Version + 1, 0);
        CheckPages(pages, expected);

        slice = bufferWriter.ConsumeSlice(PageSize);
        CheckPages(pages, expected);
        slice.Dispose();

        expected[1] = (expected[1].Version + 1, 0);
        CheckPages(pages, expected);

        slice = bufferWriter.ConsumeSlice(PageSize + 1);
        expected[3] = (expected[3].Version, expected[3].ReferenceCount + 1);
        CheckPages(pages, expected);
        slice.Dispose();

        expected[2] = (expected[2].Version + 1, 0);
        expected[3] = (expected[3].Version, expected[3].ReferenceCount - 1);
        CheckPages(pages, expected);

        Assert.Equal(randomData.Length - 1 - PageSize * 3, bufferWriter.Length);

        bufferWriter.Dispose();
        expected = expected.Take(3).Concat(expected.Skip(3).Select(e => (e.Version + 1, 0))).ToList();
        CheckPages(pages, expected);

        Assert.Throws<ObjectDisposedException>(() => bufferWriter.Length);

        static void CheckPages(List<ArcBufferPage> pages, List<(int Version, int ReferenceCount)> expectedValues)
        {
            var index = 0;
            foreach (var page in pages)
            {
                var expected = expectedValues[index];
                CheckPage(page, expected.Version, expected.ReferenceCount);
                ++index;
            }
        }

        static void CheckPage(ArcBufferPage page, int expectedVersion, int expectedRefCount)
        {
            Assert.Equal(expectedVersion, page.Version);
            Assert.Equal(expectedRefCount, page.ReferenceCount);
        }
    }

    /// <summary>
    /// Verifies that ReplenishBuffers provides correct buffer segments for socket-like reads and that all pages are eventually freed.
    /// </summary>
    [Fact]
    public void ReplenishBuffers_ProvidesSegmentsAndFreesPages()
    {
        var bufferWriter = new ArcBufferWriter();
        var randomData = new byte[PageSize * 16];
        _random.NextBytes(randomData);
        bufferWriter.Write([0]);
        var pages = new List<ArcBufferPage>();
        var firstSlice = bufferWriter.ConsumeSlice(1);
        var firstPage = firstSlice.Pages.First();
        firstSlice.Dispose();

        var buffers = new List<ArraySegment<byte>>(capacity: 16);
        var consumed = new List<ArcBuffer>();
        int[] socketReadSizes = [256, 4096, 76, 12805, 4096, 26, 8094, 12345, 1, 0, 12345];
        int[] messageReadSizes = [8, 1020, 8, 902, 8, 1203, 8, 8045, 0, 12034, 8, 1101, 8, 4096];
        var messageReadIndex = 0;

        ReadOnlySpan<byte> socket = randomData;
        foreach (var readSize in socketReadSizes)
        {
            bufferWriter.ReplenishBuffers(buffers);

            // Simulate reading from a socket.
            Read(ref socket, readSize, buffers);
            MaintainBufferList(buffers, readSize);
            bufferWriter.AdvanceWriter(readSize);

            // Add the newly allocated pages to the list for test assertion purposes.
            using (var peeked = bufferWriter.PeekSlice(bufferWriter.Length))
            {
                pages.AddRange(peeked.Pages.Where(p => !pages.Contains(p)));
            }

            // Simulate consuming the socket data.
            while (bufferWriter.Length > messageReadSizes[messageReadIndex % messageReadSizes.Length])
            {
                consumed.Add(bufferWriter.ConsumeSlice(messageReadSizes[messageReadIndex++ % messageReadSizes.Length]));
            }
        }

        consumed.Add(bufferWriter.ConsumeSlice(bufferWriter.Length));

        var totalReadSize = socketReadSizes.Sum();
        Assert.Equal(totalReadSize, consumed.Sum(c => c.Length));
        var consumedData = new byte[totalReadSize];
        var consumerSpan = consumedData.AsSpan();
        foreach (var buffer in consumed)
        {
            buffer.CopyTo(consumerSpan);
            consumerSpan = consumerSpan[buffer.Length..];
        }

        Assert.Equal(randomData[..totalReadSize], consumedData);
        foreach (var buffer in consumed)
        {
            buffer.Dispose();
        }

        bufferWriter.Dispose();

        // Check that all pages were freed.
        foreach (var page in pages)
        {
            Assert.Equal(0, page.ReferenceCount);
        }

        static void MaintainBufferList(List<ArraySegment<byte>> buffers, int readSize)
        {
            while (readSize > 0)
            {
                if (buffers[0].Count <= readSize)
                {
                    // Consume the buffer completely.
                    readSize -= buffers[0].Count;
                    buffers.RemoveAt(0);
                }
                else
                {
                    // Consume the buffer partially.
                    buffers[0] = new(buffers[0].Array, buffers[0].Offset + readSize, buffers[0].Count - readSize);
                    break;
                }
            }
        }

        static void Read(ref ReadOnlySpan<byte> socket, int readSize, List<ArraySegment<byte>> buffers)
        {
            var payload = socket[..readSize];
            socket = socket[readSize..];
            var bufferIndex = 0;
            while (!payload.IsEmpty)
            {
                var output = buffers[bufferIndex];
                var amount = Math.Min(output.Count, payload.Length);
                payload[..amount].CopyTo(output);
                payload = payload[amount..];
                ++bufferIndex;
            }
        }
    }

    /// <summary>
    /// Verifies that writing a buffer of a given size results in the correct reported length.
    /// </summary>
    [Fact]
    public void WriteBuffer_UpdatesLengthCorrectly()
    {
        using var buffer = new ArcBufferWriter();
        var data = new byte[1024];
        _random.NextBytes(data);
        buffer.Write(data);

        // Assert
        Assert.Equal(data.Length, buffer.Length);
    }

    /// <summary>
    /// Verifies that peeking at a slice returns the correct data without consuming it.
    /// </summary>
    [Fact]
    public void PeekSlice_ReturnsCorrectDataWithoutConsuming()
    {
        using var buffer = new ArcBufferWriter();
        var data = new byte[1024];
        _random.NextBytes(data);
        buffer.Write(data);

        using var peeked = buffer.PeekSlice(512);
        // Assert
        Assert.Equal(data.AsSpan(0, 512).ToArray(), peeked.ToArray());
    }

    /// <summary>
    /// Verifies that consuming a slice returns the correct data and updates the buffer length.
    /// </summary>
    [Fact]
    public void ConsumeSlice_ReturnsCorrectDataAndUpdatesLength()
    {
        using var buffer = new ArcBufferWriter();
        var data = new byte[1024];
        _random.NextBytes(data);
        buffer.Write(data);

        using var slice = buffer.ConsumeSlice(512);
        using var subSlice = slice.Slice(256, 256);
        // Assert
        Assert.Equal(data.AsSpan(0, 512).ToArray(), slice.ToArray());
        Assert.Equal(data.AsSpan(256, 256).ToArray(), subSlice.ToArray());
        Assert.Equal(data.Length - slice.Length, buffer.Length);
    }

    /// <summary>
    /// Verifies that using a slice after it has been unpinned throws an exception.
    /// </summary>
    [Fact]
    public void UseAfterFree_ThrowsException()
    {
        using var buffer = new ArcBufferWriter();
        var data = new byte[1024];
        _random.NextBytes(data);
        buffer.Write(data);

        var slice = buffer.ConsumeSlice(512);
        slice.Unpin();

        // Assert
        Assert.Throws<InvalidOperationException>(() => slice.ToArray());
    }

    /// <summary>
    /// Verifies that double unpinning a slice throws, and that buffer can be reset and disposed safely.
    /// </summary>
    [Fact]
    public void DoubleFree_ThrowsAndBufferCanBeResetAndDisposed()
    {
        var buffer = new ArcBufferWriter();
        var data = new byte[1024];
        _random.NextBytes(data);
        buffer.Write(data);

        var slice = buffer.ConsumeSlice(512);
        slice.Unpin();

        // Assert
        Assert.Throws<InvalidOperationException>(() => slice.Unpin());

        Assert.Equal(512, buffer.Length);
        buffer.Reset();
        Assert.Equal(0, buffer.Length);

        buffer.Dispose();
    }

    /// <summary>
    /// Verifies that a new buffer is empty.
    /// </summary>
    [Fact]
    public void NewBuffer_IsEmpty()
    {
        using var buffer = new ArcBufferWriter();
        // Assert
        Assert.Equal(0, buffer.Length);
    }

    /// <summary>
    /// Verifies that writing an empty buffer does not change the buffer length.
    /// </summary>
    [Fact]
    public void WriteEmptyBuffer_DoesNotChangeLength()
    {
        using var buffer = new ArcBufferWriter();
        var data = Array.Empty<byte>();
        _random.NextBytes(data);
        buffer.Write(data);

        // Assert
        Assert.Equal(0, buffer.Length);
    }

    /// <summary>
    /// Verifies that peeking at an empty buffer returns empty segments and throws when peeking past end.
    /// </summary>
    [Fact]
    public void PeekEmptyBuffer_ReturnsEmptyAndThrowsOnOverflow()
    {
        using var buffer = new ArcBufferWriter();
        using var peeked = buffer.PeekSlice(0);
        using var subSlice = peeked.Slice(0, 0);
        Assert.Empty(peeked.Pages);
        Assert.Empty(peeked.PageSegments);
        Assert.Empty(peeked.ArraySegments);
        Assert.Empty(peeked.MemorySegments);

        Assert.Empty(subSlice.Pages);
        Assert.Empty(subSlice.PageSegments);
        Assert.Empty(subSlice.ArraySegments);
        Assert.Empty(subSlice.MemorySegments);

        // Assert
        Assert.Equal(0, peeked.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.PeekSlice(1));
    }

    /// <summary>
    /// Verifies that consuming an empty buffer returns empty segments and throws when consuming past end.
    /// </summary>
    [Fact]
    public void ConsumeEmptyBuffer_ReturnsEmptyAndThrowsOnOverflow()
    {
        using var buffer = new ArcBufferWriter();
        using var slice = buffer.ConsumeSlice(0);
        using var subSlice = slice.Slice(0, 0);
        Assert.Empty(slice.Pages);
        Assert.Empty(slice.PageSegments);
        Assert.Empty(slice.ArraySegments);
        Assert.Empty(slice.MemorySegments);
        Assert.Equal(0, slice.AsReadOnlySequence().Length);

        Assert.Empty(subSlice.Pages);
        Assert.Empty(subSlice.PageSegments);
        Assert.Empty(subSlice.ArraySegments);
        Assert.Empty(subSlice.MemorySegments);
        Assert.Equal(0, subSlice.AsReadOnlySequence().Length);

        // Assert
        Assert.Equal(0, slice.Length);
        Assert.Equal(0, buffer.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.PeekSlice(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.ConsumeSlice(1));
    }

    /// <summary>
    /// Verifies that disposing a slice after consuming a full page increments the page version.
    /// </summary>
    [Fact]
    public void DisposeSliceAfterFullPageConsumption_IncrementsPageVersion()
    {
        using var bufferWriter = new ArcBufferWriter();
        var data = new byte[ArcBufferPagePool.MinimumPageSize + 1];
        _random.NextBytes(data);
        bufferWriter.Write(data);

        // Consuming the slice will cause the writer to release (unpin) those pages.
        // Since we write more than one page (MinimumPageSize), we should have at least two pages.
        // The write head will sit on the second page, leaving the first free to be consumed.
        var slice = bufferWriter.ConsumeSlice(ArcBufferPagePool.MinimumPageSize);
        var pages = new List<ArcBufferPage>(slice.Pages);

        var initialVersions = pages.Select(p => p.Version).ToList();
        slice.Dispose();

        // Assert
        foreach (var page in pages.Zip(initialVersions))
        {
            // Check that the versions have been incremented.
            Assert.True(page.First.Version > page.Second);
        }
    }

    /// <summary>
    /// Verifies that after writing and then advancing the read head, the page version is incremented as expected.
    /// </summary>
    [Fact]
    public void PageVersionIncrementAfterWriteAndReadHeadAdvance()
    {
        using var bufferWriter = new ArcBufferWriter();
        var data = new byte[ArcBufferPagePool.MinimumPageSize];
        _random.NextBytes(data);
        bufferWriter.Write(data);

        // Since we write exactly one page (MinimumPageSize), we should have exactly one page.
        // The write head will sit on the first page, preventing it from being unpinned.
        var slice = bufferWriter.ConsumeSlice(ArcBufferPagePool.MinimumPageSize);
        var pages = new List<ArcBufferPage>(slice.Pages);

        var initialVersions = pages.Select(p => p.Version).ToList();
        slice.Dispose();

        // Assert
        foreach (var page in pages.Zip(initialVersions))
        {
            // Check that the versions have NOT been incremented.
            Assert.False(page.First.Version > page.Second);
        }

        // Write one more byte, moving the write head to the second page.
        bufferWriter.Write([0]);

        // Advance the read head to trigger unpinning and version increment.
        bufferWriter.AdvanceReader(1);

        // Assert
        foreach (var page in pages.Zip(initialVersions))
        {
            // Check that the versions have NOT been incremented.
            Assert.True(page.First.Version > page.Second);
        }
    }

    /// <summary>
    /// Verifies that all operations throw ObjectDisposedException after the buffer is disposed.
    /// </summary>
    [Fact]
    public void DisposedBuffer_ThrowsOnAllOperations()
    {
        var buffer = new ArcBufferWriter();
        buffer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buffer.GetMemory(1));
        Assert.Throws<ObjectDisposedException>(() => buffer.GetSpan(1));
        Assert.Throws<ObjectDisposedException>(() => buffer.Write(new byte[1]));
        Assert.Throws<ObjectDisposedException>(() => buffer.PeekSlice(0));
        Assert.Throws<ObjectDisposedException>(() => buffer.ConsumeSlice(0));
        Assert.Throws<ObjectDisposedException>(() => buffer.AdvanceWriter(1));
        Assert.Throws<ObjectDisposedException>(() => buffer.AdvanceReader(0));
        Assert.Throws<ObjectDisposedException>(() => buffer.Reset());
        Assert.Throws<ObjectDisposedException>(() => buffer.ReplenishBuffers(new List<ArraySegment<byte>>(1)));
    }

    /// <summary>
    /// Verifies that double-disposing an ArcBuffer slice is safe and does not throw.
    /// </summary>
    [Fact]
    public void DoubleDisposeArcBuffer_IsSafe()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[100]);
        var slice = buffer.PeekSlice(10);
        slice.Dispose();
        // Should not throw
        slice.Dispose();
    }

    /// <summary>
    /// Verifies that resetting a disposed buffer throws ObjectDisposedException.
    /// </summary>
    [Fact]
    public void ResetAfterDispose_Throws()
    {
        var buffer = new ArcBufferWriter();
        buffer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buffer.Reset());
    }

    /// <summary>
    /// Verifies that advancing the writer by a negative value throws ArgumentOutOfRangeException.
    /// </summary>
    [Fact]
    public void AdvanceWriterNegative_Throws()
    {
        using var buffer = new ArcBufferWriter();
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.AdvanceWriter(-1));
    }

    /// <summary>
    /// Verifies that advancing the reader by a negative or too-large value throws ArgumentOutOfRangeException.
    /// </summary>
    [Fact]
    public void AdvanceReaderNegativeOrTooLarge_Throws()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[10]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.PeekSlice(11));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.ConsumeSlice(11));
    }

    /// <summary>
    /// Verifies that calling Reset() after writing data spanning several pages returns all pages to the pool and empties the buffer.
    /// </summary>
    [Fact]
    public void ResetReleasesAllPages_EmptiesBuffer()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[ArcBufferPagePool.MinimumPageSize * 3]);
        buffer.Reset();
        Assert.Equal(0, buffer.Length);
    }

    /// <summary>
    /// Verifies that calling Dispose() multiple times on ArcBufferWriter is safe.
    /// </summary>
    [Fact]
    public void DisposeMultipleTimes_IsSafe()
    {
        var buffer = new ArcBufferWriter();
        buffer.Dispose();
        buffer.Dispose();
    }

    /// <summary>
    /// Verifies that writing or getting memory/span after Dispose() throws ObjectDisposedException.
    /// </summary>
    [Fact]
    public void WriteAfterDispose_Throws()
    {
        var buffer = new ArcBufferWriter();
        buffer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buffer.Write(new byte[1]));
        Assert.Throws<ObjectDisposedException>(() => buffer.GetMemory(1));
        Assert.Throws<ObjectDisposedException>(() => buffer.GetSpan(1));
    }

    /// <summary>
    /// Verifies that pinning and unpinning a page multiple times only returns it to the pool when the reference count reaches zero.
    /// </summary>
    [Fact]
    public void PinUnpinReferenceCounting_WorksCorrectly()
    {
        var page = new ArcBufferPage(ArcBufferPagePool.MinimumPageSize);
        int token = page.Version;
        page.Pin(token);
        page.Pin(token);
        Assert.Equal(2, page.ReferenceCount);
        page.Unpin(token);
        Assert.Equal(1, page.ReferenceCount);
        page.Unpin(token);
        Assert.Equal(0, page.ReferenceCount);
    }

    /// <summary>
    /// Verifies that unpinning a page with an incorrect version token throws InvalidOperationException.
    /// </summary>
    [Fact]
    public void UnpinWithInvalidToken_Throws()
    {
        var page = new ArcBufferPage(ArcBufferPagePool.MinimumPageSize);
        int token = page.Version;
        page.Pin(token);
        Assert.Throws<InvalidOperationException>(() => page.Unpin(token + 1));
    }

    /// <summary>
    /// Verifies that CheckValidity throws if the reference count is zero or negative.
    /// </summary>
    [Fact]
    public void CheckValidityWithInvalidRefCount_Throws()
    {
        var page = new ArcBufferPage(ArcBufferPagePool.MinimumPageSize);
        int token = page.Version;
        Assert.Throws<InvalidOperationException>(() => page.CheckValidity(token));
    }

    /// <summary>
    /// Verifies that disposing a slice does not affect the original buffer.
    /// </summary>
    [Fact]
    public void SliceDispose_DoesNotAffectOriginalBuffer()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[100]);
        var slice = buffer.PeekSlice(50);
        slice.Dispose();
        Assert.Equal(100, buffer.Length);
    }

    /// <summary>
    /// Verifies that UnsafeSlice does not increment the reference count.
    /// </summary>
    [Fact]
    public void UnsafeSlice_DoesNotPinPages()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[100]);
        var slice = buffer.PeekSlice(100);
        var page = slice.First;
        int before = page.ReferenceCount;
        var unsafeSlice = slice.UnsafeSlice(10, 10);
        Assert.Equal(before, unsafeSlice.First.ReferenceCount);
    }

    /// <summary>
    /// Verifies that copying to a span that is too small throws.
    /// </summary>
    [Fact]
    public void CopyToWithInsufficientDestination_Throws()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[100]);
        var slice = buffer.PeekSlice(100);
        var dest = new byte[50];
        Assert.Throws<ArgumentException>(() => slice.CopyTo(dest.AsSpan()));
    }

    /// <summary>
    /// Verifies that consuming more bytes than available throws.
    /// </summary>
    [Fact]
    public void ConsumeMoreThanAvailable_Throws()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[10]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.ConsumeSlice(20));
    }

    /// <summary>
    /// Verifies that Skip() advances the read head.
    /// </summary>
    [Fact]
    public void SkipAdvancesReadHead_WorksCorrectly()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[100]);
        var reader = new ArcBufferReader(buffer);
        reader.Skip(50);
        Assert.Equal(50, reader.Length);
    }

    /// <summary>
    /// Verifies that large pages are reused by the pool.
    /// </summary>
    [Fact]
    public void LargePageReuse_Works()
    {
        var pool = ArcBufferPagePool.Shared;
        var page1 = pool.Rent(ArcBufferPagePool.MinimumPageSize * 4);
        int version1 = page1.Version;
        page1.Pin(version1); // Pin the page
        page1.Unpin(version1); // Return to pool
        var page2 = pool.Rent(ArcBufferPagePool.MinimumPageSize * 4);
        Assert.True(page2.Version > version1 || page2 != page1);
    }

    /// <summary>
    /// Verifies that minimum size pages are reused by the pool.
    /// </summary>
    [Fact]
    public void MinimumPageReuse_Works()
    {
        var pool = ArcBufferPagePool.Shared;
        var page1 = pool.Rent();
        int version1 = page1.Version;
        page1.Pin(version1); // Pin the page
        page1.Unpin(version1); // Return to pool
        var page2 = pool.Rent();
        Assert.True(page2.Version > version1 || page2 != page1);
    }

    /// <summary>
    /// Verifies boundary values for slicing, peeking, and consuming.
    /// </summary>
    [Fact]
    public void BoundaryValue_SlicePeekConsume()
    {
        using var buffer = new ArcBufferWriter();
        var data = new byte[PageSize * 2];
        _random.NextBytes(data);
        buffer.Write(data);

        // Slice at start
        using (var s = buffer.PeekSlice(0))
        {
            Assert.Equal(0, s.Length);
        }
        using (var s = buffer.PeekSlice(1))
        {
            Assert.Equal(data[0], s.ToArray()[0]);
        }
        using (var s = buffer.PeekSlice(data.Length))
        {
            Assert.Equal(data, s.ToArray());
        }

        // Slice at page boundary
        using (var s = buffer.PeekSlice(PageSize))
        {
            Assert.Equal(data.Take(PageSize).ToArray(), s.ToArray());
        }
        using (var s = buffer.PeekSlice(PageSize + 1))
        {
            Assert.Equal(data.Take(PageSize + 1).ToArray(), s.ToArray());
        }

        // Consume at boundaries
        using (var s = buffer.ConsumeSlice(0))
        {
            Assert.Equal(0, s.Length);
        }
        using (var s = buffer.ConsumeSlice(1))
        {
            Assert.Equal(data[0], s.ToArray()[0]);
        }
        using (var s = buffer.ConsumeSlice(PageSize - 1))
        {
            Assert.Equal(data.Skip(1).Take(PageSize - 1).ToArray(), s.ToArray());
        }
        using (var s = buffer.ConsumeSlice(PageSize))
        {
            Assert.Equal(data.Skip(PageSize).Take(PageSize).ToArray(), s.ToArray());
        }
    }

    /// <summary>
    /// Verifies that double-free and use-after-free are guarded.
    /// </summary>
    [Fact]
    public void DoubleFree_And_UseAfterFree_Guards()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[100]);
        var slice = buffer.PeekSlice(50);
        slice.Dispose();
        // Double dispose is safe
        slice.Dispose();
        // Unpin after dispose throws
        Assert.Throws<InvalidOperationException>(() => slice.Unpin());
        // Use after dispose throws
        Assert.Throws<InvalidOperationException>(() => slice.ToArray());
    }

    /// <summary>
    /// Verifies that memory is not leaked (reference count returns to zero) after all slices are disposed.
    /// </summary>
    [Fact]
    public void NoMemoryLeak_ReferenceCountReturnsToZero()
    {
        var buffer = new ArcBufferWriter();
        buffer.Write(new byte[PageSize * 2]);
        var slices = new List<ArcBuffer>();
        for (int i = 0; i < 10; i++)
        {
            slices.Add(buffer.PeekSlice(PageSize));
        }
        var pages = slices[0].Pages.ToList();
        foreach (var s in slices)
        {
            s.Dispose();
        }
        foreach (var p in pages)
        {
            Assert.Equal(1, p.ReferenceCount); // Only the buffer's own pin remains
        }
        buffer.Dispose();
        foreach (var p in pages)
        {
            Assert.Equal(0, p.ReferenceCount);
        }
    }

    /// <summary>
    /// Verifies that slicing and peeking with zero-length and full-length works for empty and full buffers.
    /// </summary>
    [Fact]
    public void EmptyAndFullBuffer_SlicePeek()
    {
        using var buffer = new ArcBufferWriter();
        using (var s = buffer.PeekSlice(0))
        {
            Assert.Equal(0, s.Length);
        }
        buffer.Write(new byte[PageSize]);
        using (var s = buffer.PeekSlice(PageSize))
        {
            Assert.Equal(PageSize, s.Length);
        }
        using (var s = buffer.ConsumeSlice(PageSize))
        {
            Assert.Equal(PageSize, s.Length);
        }
        Assert.Equal(0, buffer.Length);
    }

    /// <summary>
    /// Verifies that slicing at the very end of the buffer returns an empty slice.
    /// </summary>
    [Fact]
    public void SliceAtEnd_ReturnsEmpty()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[10]);
        buffer.ConsumeSlice(10).Dispose();
        using (var s = buffer.PeekSlice(0))
        {
            Assert.Equal(0, s.Length);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.PeekSlice(1));
    }

    /// <summary>
    /// Verifies that pin/unpin on different slices to the same page does not leak memory.
    /// </summary>
    [Fact]
    public void MultipleSlices_SamePage_NoLeak()
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[PageSize]);
        var s1 = buffer.PeekSlice(PageSize / 2);
        var s2 = buffer.PeekSlice(PageSize / 2);
        var page = s1.First;
        Assert.True(page.ReferenceCount >= 2);
        s1.Dispose();
        Assert.True(page.ReferenceCount >= 1);
        s2.Dispose();
        Assert.Equal(1, page.ReferenceCount); // Only buffer's own pin remains
        buffer.Dispose();
        Assert.Equal(0, page.ReferenceCount);
    }
}
