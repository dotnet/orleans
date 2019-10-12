using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Orleans.Providers.Streams.Common;

namespace Benchmarks.PooledQueueCacheBenchmark
{
    public class PooledQueueCacheBenchmarks
    {
        private const int NumberOfStream = 1 << 6;
        private const int NumberOfConsumersPerStream = 1 << 2;
        private const int EventsPersStream = 1 << 7;
        private const int EventsGeneratedPerCycle = 1 << 5;
        private const int EventPayloadSizeInBytes = 1 << 10;
        private static readonly byte[] Payload = new byte[EventPayloadSizeInBytes];

        private PooledQueueCache cache;
        private object[] cursors;
        private List<List<CachedMessage>> chunks;
        private int consumed;
        private int removed;

        public void BenchmarkSetup()
        {
            this.cache = new PooledQueueCache(null, null);
            byte[][] streams = Enumerable.Range(0, NumberOfStream)
             .Select(streamId => BitConverter.GetBytes(streamId))
             .ToArray();
            Console.WriteLine($"streams {streams.Length}");
            byte[] startToken = BitConverter.GetBytes(0);
            this.cursors = streams
                .SelectMany(streamId => Enumerable.Range(0, NumberOfConsumersPerStream)
                    .Select(_ => cache.GetCursor(streamId, startToken)))
                .ToArray();
            Console.WriteLine($"cursors {cursors.Length}");
            var generated = Enumerable.Range(0, EventsPersStream * NumberOfStream)
                .Select(sequenceNumber => CachedMessage.Create(
                    BitConverter.GetBytes(IPAddress.HostToNetworkOrder(sequenceNumber)),
                    streams[sequenceNumber % streams.Length],
                    BitConverter.GetBytes(sequenceNumber),
                    Payload,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    size => new ArraySegment<byte>(new byte[size])))
                .ToArray();
            this.chunks = new List<List<CachedMessage>>();
            while (generated.Any())
            {
                this.chunks.Add(generated.Take(EventsGeneratedPerCycle).ToList());
                generated = generated.Skip(EventsGeneratedPerCycle).ToArray();
            }
            Console.WriteLine($"chunks {this.chunks.Count}");
            this.consumed = 0;
        }

        public void Run()
        {
            foreach(List<CachedMessage> chunk in this.chunks)
            {
                this.cache.Add(chunk, DateTime.Now);
                RunCursors();
                while (this.cache.Newest.HasValue)
                {
                    this.cache.RemoveOldestMessage();
                    this.removed++;
                }
            }
        }

        public void Teardown()
        {
            Console.WriteLine($"Consumed {this.consumed}");
            Console.WriteLine($"Removed {this.removed}");
            this.cache = null;
            this.cursors = null;
            this.chunks = null;
            this.consumed = 0;
        }

        private void RunCursors()
        {
            foreach (object cursor in this.cursors)
            {
                while (this.cache.TryGetNextMessage(cursor, out CachedMessage message))
                {
                    this.consumed++;
                }
            }
        }
    }
}