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
    /// <summary>
    /// Creates Event Hub partition checkpointers backed by Azure Table Storage.
    /// </summary>
    public class EventHubCheckpointerFactory : IStreamQueueCheckpointerFactory
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly string providerName;
        private readonly AzureTableStreamCheckpointerOptions options;
        private readonly ClusterOptions clusterOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventHubCheckpointerFactory"/> class.
        /// </summary>
        /// <param name="providerName">The stream provider name.</param>
        /// <param name="options">The Azure Table Storage checkpointer options.</param>
        /// <param name="clusterOptions">The cluster options.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public EventHubCheckpointerFactory(string providerName, AzureTableStreamCheckpointerOptions options, IOptions<ClusterOptions> clusterOptions, ILoggerFactory loggerFactory)
        {
            this.options = options;
            this.clusterOptions = clusterOptions.Value;
            this.loggerFactory = loggerFactory;
            this.providerName = providerName;
        }

        /// <inheritdoc />
        public Task<IStreamQueueCheckpointer<string>> Create(string partition)
        {
            return EventHubCheckpointer.Create(options, providerName, partition, this.clusterOptions.ServiceId.ToString(), loggerFactory);
        }

        /// <summary>
        /// Creates an Event Hub partition checkpointer factory.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="providerName">The stream provider name.</param>
        /// <returns>The checkpointer factory.</returns>
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

        /// <inheritdoc />
        public bool CheckpointExists => _inner.CheckpointExists;

        /// <summary>
        /// Creates and initializes an Event Hub partition checkpointer.
        /// </summary>
        /// <param name="options">The Azure Table Storage checkpointer options.</param>
        /// <param name="streamProviderName">The stream provider name.</param>
        /// <param name="partition">The Event Hub partition identifier.</param>
        /// <param name="serviceId">The Orleans service identifier.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <returns>A task which resolves to the initialized checkpointer.</returns>
        public static async Task<IStreamQueueCheckpointer<string>> Create(AzureTableStreamCheckpointerOptions options, string streamProviderName, string partition, string serviceId, ILoggerFactory loggerFactory)
        {
            var inner = await AzureTableStreamQueueCheckpointer.Create(
                options,
                streamProviderName,
                partition,
                serviceId,
                loggerFactory,
                StreamCheckpointComparers.Numeric,
                StreamQueueCheckpointEntity.EventHubPartitionKeyPrefix);
            return new EventHubCheckpointer(inner);
        }

        private EventHubCheckpointer(IStreamQueueCheckpointer<string> inner)
        {
            _inner = inner;
        }

        /// <summary>
        /// Loads the checkpoint.
        /// </summary>
        /// <returns>A task which resolves to the checkpoint offset.</returns>
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public Task<string> Load() => Load(CancellationToken.None);

        /// <summary>
        /// Loads a checkpoint.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The checkpoint.</returns>
        public async Task<string> Load(CancellationToken cancellationToken)
        {
            var checkpoint = await _inner.Load(cancellationToken);
            return CheckpointExists ? checkpoint : EventHubConstants.StartOfStream;
        }

        /// <inheritdoc />
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public Task Reset() => Reset(CancellationToken.None);

        /// <inheritdoc />
        public Task Reset(CancellationToken cancellationToken) => _inner.Reset(cancellationToken);

        /// <summary>
        /// Updates the checkpoint.  This is a best effort.  It does not always update the checkpoint.
        /// The latest offset is always tracked in memory so that <see cref="FlushAsync(CancellationToken)"/> can persist it on shutdown.
        /// </summary>
        /// <param name="offset">The checkpoint offset.</param>
        /// <param name="utcNow">The current UTC time.</param>
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public void Update(string offset, DateTime utcNow)
            => Update(offset, utcNow, CancellationToken.None);

        /// <summary>
        /// Updates the checkpoint.
        /// </summary>
        /// <param name="offset">The checkpoint offset.</param>
        /// <param name="utcNow">The current UTC time.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken)
            => _inner.Update(offset, utcNow, cancellationToken);

        /// <summary>
        /// Flushes any pending checkpoint to persistent storage.
        /// Awaits any in-progress save, then persists the latest offset if it has advanced beyond the last saved value.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task which represents the flush operation.</returns>
        public Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    }
}
