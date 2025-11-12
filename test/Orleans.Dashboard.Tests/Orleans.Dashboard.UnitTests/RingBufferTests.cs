using Orleans.Dashboard.Metrics.History;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace UnitTests
{
    public class RingBufferTests
    {
        [Fact]
        public void Should_create_empty_buffer()
        {
            var buffer = new RingBuffer<int>(10);

            Assert.Empty(ToList(buffer));
        }

        [Fact]
        public void Should_add_item_to_buffer_until_capacity_reached()
        {
            var buffer = new RingBuffer<int>(10);

            for (var i = 0; i < 10; i++)
            {
                buffer.Add(i);

                Assert.Equal(Enumerable.Range(0, i + 1).ToArray(), ToList(buffer).ToArray());
            }
        }

        [Fact]
        public void Should_add_item_over_capacity()
        {
            var buffer = new RingBuffer<int>(10);

            for (var i = 0; i < 100; i++)
            {
                buffer.Add(i);

                Assert.Equal(Enumerable.Range(0, i + 1).TakeLast(10).ToArray(), ToList(buffer).ToArray());
            }
        }

        private static List<T> ToList<T>(RingBuffer<T> buffer)
        {
            var result = new List<T>(); 

            for (var i = 0; i < buffer.Count; i++)
            {
                result.Add(buffer[i]);
            }

            return result;
        }
    }
}
