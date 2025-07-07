using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Serialization.UnitTests
{
    /// <summary>
    /// Tests for Orleans' PooledBuffer implementation.
    /// 
    /// PooledBuffer is a high-performance buffer management system that:
    /// - Uses ArrayPool to minimize allocations and GC pressure
    /// - Supports efficient slicing operations without copying
    /// - Handles large data through segmented storage
    /// - Provides zero-copy access to buffer contents
    /// 
    /// Key features tested:
    /// - Large buffer handling (multi-megabyte)
    /// - Slicing operations at various offsets
    /// - Memory safety and bounds checking
    /// - Proper cleanup and return to pool
    /// 
    /// This infrastructure is critical for Orleans' serialization performance,
    /// especially when handling large object graphs or streaming scenarios.
    /// </summary>
    [Trait("Category", "BVT")]
    public class PooledBufferTests
    {
        [Fact]
        public void LargeBufferRoundTrip()
        {
            var random = new Random();
            var buffer = new PooledBuffer();
            var randomData = new byte[1024 * 1024 * 10];
            random.NextBytes(randomData);
            buffer.Write(randomData);

            var slice4 = buffer.Slice(3000, 1500);
            var sliceArray4 = slice4.ToArray();
            Assert.True(randomData.AsSpan(3000, 1500).SequenceEqual(sliceArray4));

            var slice = buffer.Slice();
            var sliceArray = slice.ToArray();
            Assert.True(randomData.AsSpan().SequenceEqual(sliceArray));

            var slice3 = buffer.Slice(100, 1024);
            var sliceArray3 = slice3.ToArray();
            Assert.True(randomData.AsSpan(100, 1024).SequenceEqual(sliceArray3));

            var slice2 = buffer.Slice(100);
            var sliceArray2 = slice2.ToArray();
            var slicedRandomData = randomData.AsSpan(100).ToArray();
            Assert.True(slicedRandomData.AsSpan().SequenceEqual(sliceArray2));

            var rosArray = new byte[randomData.Length];
            buffer.AsReadOnlySequence().CopyTo(rosArray.AsSpan());
            Assert.True(randomData.AsSpan().SequenceEqual(rosArray));

            var spansArray = new byte[randomData.Length];
            var spansArraySpan = spansArray.AsSpan();
            foreach (var span in buffer.Slice())
            {
                span.CopyTo(spansArraySpan);
                spansArraySpan = spansArraySpan[span.Length..];
            }

            Assert.True(randomData.AsSpan().SequenceEqual(spansArray));

            buffer.Dispose();
        }

        [Fact]
        public void LargeBufferRoundTrip_Single()
        {
            var random = new Random();
            var buffer = new PooledBuffer();
            var randomData = new byte[1024 * 1024 * 10];
            random.NextBytes(randomData);
            buffer.Write(randomData);

            var contents = buffer.ToArray();
            Assert.True(randomData.AsSpan().SequenceEqual(contents));

            buffer.Dispose();
        }

        [Fact]
        public void LargeBufferRoundTrip_ReaderWriter()
        {
            var random = new Random();
            var randomData = new byte[1024 * 1024 * 10];
            random.NextBytes(randomData);
            var writer = Writer.Create(new PooledBuffer(), null);
            writer.Write(randomData);
            writer.Commit();

            var slice = writer.Output.Slice();
            var sliceReader = Reader.Create(slice, null);
            var sliceArray = sliceReader.ReadBytes((uint)randomData.Length);
            Assert.True(randomData.AsSpan().SequenceEqual(sliceArray));

            var slice3 = writer.Output.Slice(100, 1024);
            var reader3 = Reader.Create(slice3, null);
            var result3 = reader3.ReadBytes((uint)slice3.Length);
            Assert.True(randomData.AsSpan(100, 1024).SequenceEqual(result3));

            var slice2 = writer.Output.Slice(100);
            var reader2 = Reader.Create(slice2, null);
            var result2 = reader2.ReadBytes((uint)slice2.Length);
            Assert.True(randomData.AsSpan(100).SequenceEqual(result2));

            var slice4 = writer.Output.Slice(3000, 1500);
            var reader4 = Reader.Create(slice4, null);
            var result4 = reader4.ReadBytes((uint)slice4.Length);
            Assert.True(randomData.AsSpan(3000, 1500).SequenceEqual(result4));

            var slice5 = writer.Output.Slice(4500, 125);
            var reader5 = Reader.Create(slice5, null);
            var result5 = reader5.ReadBytes((uint)slice5.Length);
            Assert.True(randomData.AsSpan(4500, 125).SequenceEqual(result5));

            var ros = writer.Output.AsReadOnlySequence();
            var rosReader = Reader.Create(ros, null);
            var rosArray = rosReader.ReadBytes((uint)randomData.Length);
            Assert.True(randomData.AsSpan().SequenceEqual(rosArray));

            writer.Dispose();
        }

        /// <summary>
        /// Regression test for https://github.com/dotnet/orleans/issues/8503
        /// </summary>
        [Fact]
        public void PooledBuffer_WriteTwice()
        {
            var serviceProvider = new ServiceCollection()
                .AddSerializer()
                .BuildServiceProvider();
            var pool = serviceProvider.GetRequiredService<SerializerSessionPool>();
            var serializer = serviceProvider.GetRequiredService<Serializer>();
            var obj = LargeObject.BuildRandom();

            SerializeObject(pool, serializer, obj);
            SerializeObject(pool, serializer, obj);

            static void SerializeObject(SerializerSessionPool pool, Serializer serializer, LargeObject obj)
            {
                Writer<PooledBuffer> writer = default;
                var session = pool.GetSession();
                try
                {
                    writer = Writer.CreatePooled(session);
                    serializer.Serialize(obj, ref writer);

                    var sequence = writer.Output.AsReadOnlySequence();
                    Assert.Equal(writer.Output.Length, sequence.Length);
                }
                finally
                {
                    writer.Dispose();
                    session.Dispose();
                }
            }
        }

        /// <summary>
        /// Tests that the serializer can correctly serialized <see cref="PooledBuffer"/>.
        /// </summary>
        [Fact]
        public void PooledBuffer_SerializerRoundTrip()
        {
            var serviceProvider = new ServiceCollection()
                .AddSerializer()
                .BuildServiceProvider();
            var serializer = serviceProvider.GetRequiredService<Serializer>();

            var random = new Random();
            for (var i = 0; i < 10; i++)
            {
                const int TargetLength = 8120;

                // NOTE: The serializer is responsible for freeing the buffer provided to it, so we do not free this.
                var buffer = new PooledBuffer();
                while (buffer.Length < TargetLength)
                {
                    var span = buffer.GetSpan(TargetLength - buffer.Length);
                    var writeLen = Math.Min(span.Length, TargetLength - buffer.Length);
                    random.NextBytes(span[..writeLen]);
                    buffer.Advance(writeLen);
                }

                var bytes = buffer.ToArray();
                Assert.Equal(TargetLength, bytes.Length);

                var result = serializer.Deserialize<PooledBuffer>(serializer.SerializeToArray(buffer));
                Assert.Equal(TargetLength, result.Length);

                var resultBytes = result.ToArray();
                Assert.Equal(bytes, resultBytes);

                // NOTE: we are responsible for disposing a buffer returned from deserialization.
                result.Dispose();
            }
        }

        /// <summary>
        /// Tests that the serializer can correctly serialized <see cref="PooledBuffer"/> when it's embedded in another structure.
        /// </summary>
        [Fact]
        public void PooledBuffer_SerializerRoundTrip_Embedded()
        {
            var serviceProvider = new ServiceCollection()
                .AddSerializer()
                .BuildServiceProvider();
            var serializer = serviceProvider.GetRequiredService<Serializer>();

            var random = new Random();
            for (var i = 0; i < 10; i++)
            {
                const int TargetLength = 8120;

                // NOTE: The serializer is responsible for freeing the buffer provided to it, so we do not free this.
                var buffer = new PooledBuffer();
                while (buffer.Length < TargetLength)
                {
                    var span = buffer.GetSpan(TargetLength - buffer.Length);
                    var writeLen = Math.Min(span.Length, TargetLength - buffer.Length);
                    random.NextBytes(span[..writeLen]);
                    buffer.Advance(writeLen);
                }

                var bytes = buffer.ToArray();
                Assert.Equal(TargetLength, bytes.Length);

                var embed = (Guid: Guid.NewGuid(), Buffer: buffer, Int: 42);
                var result = serializer.Deserialize<(Guid Guid, PooledBuffer Buffer, int Int)>(serializer.SerializeToArray(embed));
                Assert.Equal(embed.Guid, result.Guid);
                Assert.Equal(embed.Int, result.Int);
                var resultBuffer = result.Buffer;
                Assert.Equal(TargetLength, resultBuffer.Length);

                var resultBytes = resultBuffer.ToArray();
                Assert.Equal(bytes, resultBytes);

                // NOTE: we are responsible for disposing a buffer returned from deserialization.
                resultBuffer.Dispose();
            }
        }

        [GenerateSerializer]
        public readonly record struct LargeObject(
            [property: Id(0)] Guid Id,
            [property: Id(1)] (Guid, Guid)[] Values)
        {
            public static LargeObject BuildRandom()
            {
                var id = Guid.NewGuid();
                var values = new (Guid, Guid)[256];

                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = (Guid.NewGuid(), Guid.NewGuid());
                }

                return new(id, values);
            }
        }
    }
}
