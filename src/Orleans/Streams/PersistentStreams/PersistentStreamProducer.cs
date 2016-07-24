using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal class PersistentStreamProducer<T> : IInternalAsyncBatchObserver<T>
    {
        private readonly StreamImpl<T> stream;
        private readonly IQueueAdapter queueAdapter;

        internal bool IsRewindable { get; private set; }

        internal PersistentStreamProducer(StreamImpl<T> stream, IStreamProviderRuntime providerUtilities, IQueueAdapter queueAdapter, bool isRewindable)
        {
            this.stream = stream;
            this.queueAdapter = queueAdapter;
            IsRewindable = isRewindable;
            var logger = providerUtilities.GetLogger(this.GetType().Name);
            if (logger.IsVerbose) logger.Verbose("Created PersistentStreamProducer for stream {0}, of type {1}, and with Adapter: {2}.",
                stream.ToString(), typeof (T), this.queueAdapter.Name);
        }

        public Task OnNextAsync(T item, StreamSequenceToken token)
        {
            return queueAdapter.QueueMessageAsync(stream.StreamId.Guid, stream.StreamId.Namespace, item, token, Runtime.RequestContext.Export());
        }

        public Task OnNextBatchAsync(IEnumerable<T> batch, StreamSequenceToken token)
        {
            return queueAdapter.QueueMessageBatchAsync(stream.StreamId.Guid, stream.StreamId.Namespace, batch, token, Runtime.RequestContext.Export());
        }

        public Task OnCompletedAsync()
        {
            // Maybe send a close message to the rendezvous?
            throw new NotImplementedException("OnCompletedAsync is not implemented for now.");
        }

        public Task OnErrorAsync(Exception ex)
        {
            // Maybe send a close message to the rendezvous?
            throw new NotImplementedException("OnErrorAsync is not implemented for now.");
        }

        public Task Cleanup()
        {
            return TaskDone.Done;
        }
    }
}
