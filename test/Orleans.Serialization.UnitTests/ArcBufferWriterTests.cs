using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Serialization.UnitTests
{
    [Trait("Category", "BVT")]
    public class ArcBufferWriterTests
    {
        private const int PageSize = ArcBufferWriter.MinimumPageSize;
#if NET6_0_OR_GREATER
        private readonly Random Random = Random.Shared;
#else
        private readonly Random Random = new Random();
#endif
        [Fact]
        public void TestMultiPageBuffer()
        {
            using var bufferWriter = new ArcBufferWriter();
            var randomData = new byte[PageSize * 3];
            Random.NextBytes(randomData);
            int[] writeSizes = [1, 52, 125, 4096];
            var i = 0;
            while (bufferWriter.UnconsumedLength < randomData.Length)
            {
                var writeSize = Math.Min(randomData.Length - bufferWriter.UnconsumedLength, writeSizes[i++ % writeSizes.Length]);
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
                    var copiedData = new byte[newWriter.UnconsumedLength];
                    newWriter.Peek(copiedData);
                    newWriter.AdvanceReader(copiedData.Length);
                    Assert.Equal(0, newWriter.UnconsumedLength);
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

            Assert.Equal(randomData.Length, bufferWriter.UnconsumedLength);

            {
                using var peeked = bufferWriter.PeekSlice(3000);
                using var slice = bufferWriter.ConsumeSlice(3000);
                var sliceArray = slice.ToArray();
                Assert.Equal(randomData.AsSpan(0, 3000).ToArray(), sliceArray);
                Assert.Equal(sliceArray, peeked.ToArray());
                Assert.Equal(sliceArray, peeked.AsReadOnlySequence().ToArray());

                Assert.Equal(randomData.Length - sliceArray.Length, bufferWriter.UnconsumedLength);
            }

            {
                using var peeked = bufferWriter.PeekSlice(3000);
                using var slice = bufferWriter.ConsumeSlice(3000);
                var sliceArray = slice.ToArray();
                Assert.Equal(randomData.AsSpan(3000, 3000).ToArray(), sliceArray);
                Assert.Equal(sliceArray, peeked.ToArray());
                Assert.Equal(sliceArray, slice.AsReadOnlySequence().ToArray());

                Assert.Equal(randomData.Length - sliceArray.Length * 2, bufferWriter.UnconsumedLength);
            }

            Assert.Equal(randomData.Length - 6000, bufferWriter.UnconsumedLength);
        }

        [Fact]
        public void TestMultiPageBufferManagement()
        {
            var bufferWriter = new ArcBufferWriter();
            var randomData = new byte[PageSize * 12];
            Random.NextBytes(randomData);
            bufferWriter.Write(randomData);

            var peeked = bufferWriter.PeekSlice(randomData.Length);
            var pages = peeked.Pages.ToList();
            peeked.Dispose();

            var expected = pages.Select((p, i) => (Version: p.Version, ReferenceCount: p.ReferenceCount)).ToList();
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

            Assert.Equal(randomData.Length - 1 - PageSize * 3, bufferWriter.UnconsumedLength);

            bufferWriter.Dispose();
            expected = expected.Take(3).Concat(expected.Skip(3).Select(e => (e.Version + 1, 0))).ToList();
            CheckPages(pages, expected);

            Assert.Equal(0, bufferWriter.UnconsumedLength);

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

        [Fact]
        public void TestReplenishBuffers()
        {
            var bufferWriter = new ArcBufferWriter();
            var randomData = new byte[PageSize * 16];
            Random.NextBytes(randomData);
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
                using var peeked = bufferWriter.PeekSlice(bufferWriter.UnconsumedLength);
                pages.AddRange(peeked.Pages.Where(p => !pages.Contains(p)));

                // Simulate consuming the socket data.
                while (bufferWriter.UnconsumedLength > messageReadSizes[messageReadIndex % messageReadSizes.Length])
                {
                    consumed.Add(bufferWriter.ConsumeSlice(messageReadSizes[messageReadIndex++ % messageReadSizes.Length]));
                }
            }

            consumed.Add(bufferWriter.ConsumeSlice(bufferWriter.UnconsumedLength));

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

        [Fact]
        public void TestWritingBuffers()
        {
            using var buffer = new ArcBufferWriter();
            var data = new byte[1024];
            Random.NextBytes(data);
            buffer.Write(data);

            // Assert
            Assert.Equal(data.Length, buffer.UnconsumedLength);
        }

        [Fact]
        public void TestPeekingAtSlices()
        {
            using var buffer = new ArcBufferWriter();
            var data = new byte[1024];
            Random.NextBytes(data);
            buffer.Write(data);

            using var peeked = buffer.PeekSlice(512);

            // Assert
            Assert.Equal(data.AsSpan(0, 512).ToArray(), peeked.ToArray());
        }

        [Fact]
        public void TestConsumingSlices()
        {
            using var buffer = new ArcBufferWriter();
            var data = new byte[1024];
            Random.NextBytes(data);
            buffer.Write(data);

            using var slice = buffer.ConsumeSlice(512);
            using var subSlice = slice.Slice(256, 256);

            // Assert
            Assert.Equal(data.AsSpan(0, 512).ToArray(), slice.ToArray());
            Assert.Equal(data.AsSpan(256, 256).ToArray(), subSlice.ToArray());
            Assert.Equal(data.Length - slice.Length, buffer.UnconsumedLength);
        }

        [Fact]
        public void TestUseAfterFreeViolation()
        {
            using var buffer = new ArcBufferWriter();
            var data = new byte[1024];
            Random.NextBytes(data);
            buffer.Write(data);

            var slice = buffer.ConsumeSlice(512);
            slice.Unpin();

            // Assert
            Assert.Throws<InvalidOperationException>(() => slice.ToArray());
        }

        [Fact]
        public void TestDoubleFreeViolation()
        {
            var buffer = new ArcBufferWriter();
            var data = new byte[1024];
            Random.NextBytes(data);
            buffer.Write(data);

            var slice = buffer.ConsumeSlice(512);
            slice.Unpin();

            // Assert
            Assert.Throws<InvalidOperationException>(() => slice.Unpin());

            Assert.Equal(512, buffer.UnconsumedLength);
            buffer.Reset();
            Assert.Equal(0, buffer.UnconsumedLength);

            buffer.Dispose();
        }

        [Fact]
        public void TestEmptyBuffer()
        {
            using var buffer = new ArcBufferWriter();

            // Assert
            Assert.Equal(0, buffer.UnconsumedLength);
        }

        [Fact]
        public void TestWritingEmptyBuffer()
        {
            using var buffer = new ArcBufferWriter();
            var data = new byte[0];
            Random.NextBytes(data);
            buffer.Write(data);

            // Assert
            Assert.Equal(0, buffer.UnconsumedLength);
        }

        [Fact]
        public void TestPeekingAtEmptyBuffer()
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

        [Fact]
        public void TestConsumingEmptyBuffer()
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
            Assert.Equal(0, buffer.UnconsumedLength);
            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.PeekSlice(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.ConsumeSlice(1));
        }

        [Fact]
        public void TestDisposalReturnsPagesToPoolAndIncrementsVersion()
        {
            using var bufferWriter = new ArcBufferWriter();
            var data = new byte[ArcBufferWriter.MinimumPageSize * 2];
            Random.NextBytes(data);
            bufferWriter.Write(data);

            var slice = bufferWriter.ConsumeSlice(ArcBufferWriter.MinimumPageSize);
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
    }
}