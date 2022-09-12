using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Stores all streams associated with a specific silo
    /// </summary>
    internal class StreamDirectory : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<QualifiedStreamId, object> allStreams = new ConcurrentDictionary<QualifiedStreamId, object>();

        internal IAsyncStream<T> GetOrAddStream<T>(QualifiedStreamId streamId, Func<IAsyncStream<T>> streamCreator)
        {
            var stream = allStreams.GetOrAdd(streamId, (_, streamCreator) => streamCreator(), streamCreator);
            var streamOfT = stream as IAsyncStream<T>;
            if (streamOfT == null)
            {
                throw new Runtime.OrleansException($"Stream type mismatch. A stream can only support a single type of data. The generic type of the stream requested ({typeof(T)}) does not match the previously requested type ({stream.GetType().GetGenericArguments().FirstOrDefault()}).");
            }

            return streamOfT;
        }

        internal async Task Cleanup(bool cleanupProducers, bool cleanupConsumers)
        {
            if (StreamResourceTestControl.TestOnlySuppressStreamCleanupOnDeactivate)
            {
                return;
            }

            var promises = new List<Task>();
            List<QualifiedStreamId> streamIds = GetUsedStreamIds();
            foreach (QualifiedStreamId s in streamIds)
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

        private IStreamControl GetStreamControl(QualifiedStreamId streamId)
        {
            object streamObj;
            bool ok = allStreams.TryGetValue(streamId, out streamObj);
            return ok ? streamObj as IStreamControl : null;
        }

        private List<QualifiedStreamId> GetUsedStreamIds()
        {
            return allStreams.Select(kv => kv.Key).ToList();
        }

        public async ValueTask DisposeAsync() => await this.Cleanup(cleanupProducers: true, cleanupConsumers: false).ConfigureAwait(false);
    }
}
