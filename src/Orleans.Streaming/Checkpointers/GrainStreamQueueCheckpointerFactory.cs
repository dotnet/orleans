using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;

namespace Orleans.Streams
{
    /// <summary>
    /// Creates grain-based stream queue checkpointers.
    /// </summary>
    public class GrainStreamQueueCheckpointerFactory : IStreamQueueCheckpointerFactory
    {
        private readonly string _providerName;
        private readonly IClusterClient _clusterClient;
        private readonly ClusterOptions _clusterOptions;
        private readonly GrainStreamQueueCheckpointerOptions _checkpointerOptions;

        /// <summary>
        /// Initializes a new instance with default checkpointer options.
        /// </summary>
        public GrainStreamQueueCheckpointerFactory(string providerName, IOptions<ClusterOptions> clusterOptions, IClusterClient clusterClient)
            : this(providerName, clusterOptions, clusterClient, new GrainStreamQueueCheckpointerOptions())
        {
        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public GrainStreamQueueCheckpointerFactory(
            string providerName,
            IOptions<ClusterOptions> clusterOptions,
            IClusterClient clusterClient,
            GrainStreamQueueCheckpointerOptions checkpointerOptions)
        {
            _providerName = providerName;
            _clusterClient = clusterClient;
            _clusterOptions = clusterOptions.Value;
            _checkpointerOptions = checkpointerOptions;
        }

        /// <summary>
        /// Creates a factory from a service provider.
        /// </summary>
        public static IStreamQueueCheckpointerFactory CreateFactory(IServiceProvider services, string providerName)
        {
            var options = services.GetOptionsByName<GrainStreamQueueCheckpointerOptions>(providerName);
            var clusterOptions = services.GetProviderClusterOptions(providerName);
            return ActivatorUtilities.CreateInstance<GrainStreamQueueCheckpointerFactory>(
                services,
                providerName,
                clusterOptions,
                options);
        }

        /// <inheritdoc />
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public Task<IStreamQueueCheckpointer<string>> Create(string partition)
            => Create(partition, CancellationToken.None);

        /// <inheritdoc />
        public Task<IStreamQueueCheckpointer<string>> Create(string partition, CancellationToken cancellationToken)
        {
            return GrainStreamQueueCheckpointer.Create(
                _providerName,
                partition,
                _clusterOptions.ServiceId.ToString(),
                _clusterClient,
                _checkpointerOptions,
                cancellationToken);
        }
    }
}
