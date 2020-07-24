using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace Orleans.Streams
{
    internal class PersistentStreamProducer<T> : IInternalAsyncBatchObserver<T>
    {
        private readonly StreamImpl<T> stream;
        private readonly IQueueAdapter queueAdapter;
        private readonly SerializationManager serializationManager;

        internal bool IsRewindable { get; private set; }

        internal PersistentStreamProducer(StreamImpl<T> stream, IStreamProviderRuntime providerUtilities, IQueueAdapter queueAdapter, bool isRewindable, SerializationManager serializationManager)
        {
            this.stream = stream;
            this.queueAdapter = queueAdapter;
            this.serializationManager = serializationManager;
            IsRewindable = isRewindable;
            var logger = providerUtilities.ServiceProvider.GetRequiredService<ILogger<PersistentStreamProducer<T>>>();
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Created PersistentStreamProducer for stream {0}, of type {1}, and with Adapter: {2}.",
                stream.ToString(), typeof (T), this.queueAdapter.Name);
        }

        public Task OnNextAsync(T item, StreamSequenceToken token)
        {
            return this.queueAdapter.QueueMessageAsync(this.stream.StreamId, item, token, RequestContextExtensions.Export(this.serializationManager));
        }
        
        public Task OnNextBatchAsync(IEnumerable<T> batch, StreamSequenceToken token)
        {
            return this.queueAdapter.QueueMessageBatchAsync(this.stream.StreamId, batch, token, RequestContextExtensions.Export(this.serializationManager));
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
            return Task.CompletedTask;
        }
    }
}
