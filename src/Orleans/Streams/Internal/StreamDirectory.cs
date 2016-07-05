using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Stores all streams associated with a specific grain activation.
    /// </summary>
    internal class StreamDirectory
    {
        private readonly ConcurrentDictionary<StreamId, object> allStreams;

        internal StreamDirectory()
        {
            allStreams = new ConcurrentDictionary<StreamId, object>();
        }

        internal IAsyncStream<T> GetOrAddStream<T>(StreamId streamId, Func<IAsyncStream<T>> streamCreator)
        {
            return allStreams.GetOrAdd(streamId, _ => streamCreator()) as IAsyncStream<T>;
        }

        internal async Task Cleanup(bool cleanupProducers, bool cleanupConsumers)
        {
            var promises = new List<Task>();
            List<StreamId> streamIds = GetUsedStreamIds();
            foreach (StreamId s in streamIds)
            {
                IStreamControl streamControl = GetStreamControl(s);
                if (streamControl != null)
                    promises.Add(streamControl.Cleanup(cleanupProducers, cleanupConsumers));
            }
            await Task.WhenAll(promises);
        }

        internal void Clear()
        {
            // This is a quick temporary solution to unblock testing for resource leakages for streams.
            allStreams.Clear();
        }

        private IStreamControl GetStreamControl(StreamId streamId)
        {
            object streamObj;
            bool ok = allStreams.TryGetValue(streamId, out streamObj);
            return ok ? streamObj as IStreamControl : null;
        }

        private List<StreamId> GetUsedStreamIds()
        {
            return allStreams.Select(kv => kv.Key).ToList();
        }
    }
}
