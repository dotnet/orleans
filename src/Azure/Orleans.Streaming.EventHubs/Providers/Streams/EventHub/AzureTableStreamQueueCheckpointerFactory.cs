using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;

namespace Orleans.Streams
{
    /// <summary>
    /// Creates Azure Table stream queue checkpointers.
    /// </summary>
    public class AzureTableStreamQueueCheckpointerFactory : IStreamQueueCheckpointerFactory
    {
        private readonly string _providerName;
        private readonly AzureTableStreamCheckpointerOptions _options;
        private readonly ClusterOptions _clusterOptions;
        private readonly ILoggerFactory _loggerFactory;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public AzureTableStreamQueueCheckpointerFactory(
            string providerName,
            AzureTableStreamCheckpointerOptions options,
            IOptions<ClusterOptions> clusterOptions,
            ILoggerFactory loggerFactory)
        {
            _providerName = providerName;
            _options = options;
            _clusterOptions = clusterOptions.Value;
            _loggerFactory = loggerFactory;
        }

        /// <summary>
        /// Creates a factory from a service provider.
        /// </summary>
        public static IStreamQueueCheckpointerFactory CreateFactory(IServiceProvider services, string providerName)
        {
            var options = services.GetOptionsByName<AzureTableStreamCheckpointerOptions>(providerName);
            var clusterOptions = services.GetProviderClusterOptions(providerName);
            return ActivatorUtilities.CreateInstance<AzureTableStreamQueueCheckpointerFactory>(
                services,
                providerName,
                options,
                clusterOptions);
        }

        /// <inheritdoc />
        public Task<IStreamQueueCheckpointer<string>> Create(string partition)
        {
            return AzureTableStreamQueueCheckpointer.Create(
                _options,
                _providerName,
                partition,
                _clusterOptions.ServiceId.ToString(),
                _loggerFactory);
        }
    }
}
