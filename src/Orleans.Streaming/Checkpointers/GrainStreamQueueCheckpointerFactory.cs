using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using System;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    public class GrainStreamQueueCheckpointerFactory : IStreamQueueCheckpointerFactory
    {
        private readonly string _providerName;
        private readonly IClusterClient _clusterClient;
        private readonly ClusterOptions _clusterOptions;

        public GrainStreamQueueCheckpointerFactory(string providerName, IOptions<ClusterOptions> clusterOptions, IClusterClient clusterClient)
        {
            _providerName = providerName;
            _clusterClient = clusterClient;
            _clusterOptions = clusterOptions.Value;
        }

        public static IStreamQueueCheckpointerFactory CreateFactory(IServiceProvider services, string providerName)
        {
            return ActivatorUtilities.CreateInstance<GrainStreamQueueCheckpointerFactory>(services, providerName);
        }

        public Task<IStreamQueueCheckpointer<string>> Create(string partition)
        {
            return GrainStreamQueueCheckpointer.Create(_providerName, partition, _clusterOptions.ServiceId.ToString(), _clusterClient);
        }
    }
}
