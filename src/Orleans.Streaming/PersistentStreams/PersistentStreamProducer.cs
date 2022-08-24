using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using Orleans.Serialization;

namespace Orleans.Streams
{
    internal class PersistentStreamProducer<T> : IInternalAsyncBatchObserver<T>
    {
        private readonly StreamImpl<T> stream;
        private readonly IQueueAdapter queueAdapter;
        private readonly DeepCopier deepCopier;

        internal bool IsRewindable { get; private set; }

        internal PersistentStreamProducer(StreamImpl<T> stream, IStreamProviderRuntime providerUtilities, IQueueAdapter queueAdapter, bool isRewindable, DeepCopier deepCopier)
        {
            this.stream = stream;
            this.queueAdapter = queueAdapter;
            this.deepCopier = deepCopier;
            IsRewindable = isRewindable;
            var logger = providerUtilities.ServiceProvider.GetRequiredService<ILogger<PersistentStreamProducer<T>>>();
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Created PersistentStreamProducer for stream {StreamId}, of type {ElementType}, and with Adapter: {QueueAdapterName}.",
                stream.ToString(), typeof (T), this.queueAdapter.Name);
        }

        public Task OnNextAsync(T item, StreamSequenceToken token)
        {
            return this.queueAdapter.QueueMessageAsync(this.stream.StreamId, item, token, RequestContextExtensions.Export(this.deepCopier));
        }
        
        public Task OnNextBatchAsync(IEnumerable<T> batch, StreamSequenceToken token)
        {
            return this.queueAdapter.QueueMessageBatchAsync(this.stream.StreamId, batch, token, RequestContextExtensions.Export(this.deepCopier));

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
