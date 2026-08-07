using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Overrides;

namespace Orleans.Streaming.EventHubs
{
    public class EventHubCheckpointerFactory : IStreamQueueCheckpointerFactory
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly string providerName;
        private readonly AzureTableStreamCheckpointerOptions options;
        private readonly ClusterOptions clusterOptions;

        public EventHubCheckpointerFactory(string providerName, AzureTableStreamCheckpointerOptions options, IOptions<ClusterOptions> clusterOptions, ILoggerFactory loggerFactory)
        {
            this.options = options;
            this.clusterOptions = clusterOptions.Value;
            this.loggerFactory = loggerFactory;
            this.providerName = providerName;
        }

        public Task<IStreamQueueCheckpointer<string>> Create(string partition)
        {
            return EventHubCheckpointer.Create(options, providerName, partition, this.clusterOptions.ServiceId.ToString(), loggerFactory);
        }

        public static IStreamQueueCheckpointerFactory CreateFactory(IServiceProvider services, string providerName)
        {
            var options = services.GetOptionsByName<AzureTableStreamCheckpointerOptions>(providerName);
            IOptions<ClusterOptions> clusterOptions = services.GetProviderClusterOptions(providerName);
            return ActivatorUtilities.CreateInstance<EventHubCheckpointerFactory>(services, providerName, options, clusterOptions);
        }
    }

    /// <summary>
    /// This class stores EventHub partition checkpoint information in Azure Table Storage.
    /// </summary>
    public class EventHubCheckpointer : IStreamQueueCheckpointer<string>
    {
        private readonly IStreamQueueCheckpointer<string> _inner;

        /// <summary>
        /// Indicates if a checkpoint exists
        /// </summary>
        public bool CheckpointExists => _inner.CheckpointExists;

        /// <summary>
        /// Factory function that creates and initializes the checkpointer
        /// </summary>
        /// <param name="options"></param>
        /// <param name="streamProviderName"></param>
        /// <param name="partition"></param>
        /// <param name="serviceId"></param>
        /// <param name="loggerFactory"></param>
        /// <returns></returns>
        public static async Task<IStreamQueueCheckpointer<string>> Create(AzureTableStreamCheckpointerOptions options, string streamProviderName, string partition, string serviceId, ILoggerFactory loggerFactory)
        {
            var inner = await AzureTableStreamQueueCheckpointer.Create(
                options,
                streamProviderName,
                partition,
                serviceId,
                loggerFactory,
                StreamCheckpointComparers.Numeric);
            return new EventHubCheckpointer(inner);
        }

        private EventHubCheckpointer(IStreamQueueCheckpointer<string> inner)
        {
            _inner = inner;
        }

        /// <summary>
        /// Loads a checkpoint
        /// </summary>
        /// <returns></returns>
        public async Task<string> Load()
        {
            var checkpoint = await _inner.Load();
            return CheckpointExists ? checkpoint : EventHubConstants.StartOfStream;
        }

        /// <summary>
        /// Updates the checkpoint.  This is a best effort.  It does not always update the checkpoint.
        /// The latest offset is always tracked in memory so that <see cref="FlushAsync(CancellationToken)"/> can persist it on shutdown.
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="utcNow"></param>
        public void Update(string offset, DateTime utcNow) => _inner.Update(offset, utcNow);

        /// <summary>
        /// Flushes any pending checkpoint to persistent storage.
        /// Awaits any in-progress save, then persists the latest offset if it has advanced beyond the last saved value.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        public Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    }
}
