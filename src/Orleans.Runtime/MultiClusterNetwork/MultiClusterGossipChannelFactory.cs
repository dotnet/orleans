using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.MultiClusterNetwork
{
    internal class MultiClusterGossipChannelFactory
    {
        private readonly ClusterOptions clusterOptions;
        private readonly MultiClusterOptions multiClusterOptions;
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;

        public MultiClusterGossipChannelFactory(
            IOptions<ClusterOptions> clusterOptions,
            IOptions<MultiClusterOptions> multiClusterOptions, 
            IServiceProvider serviceProvider, 
            ILogger<MultiClusterGossipChannelFactory> logger)
        {
            this.clusterOptions = clusterOptions.Value;
            this.multiClusterOptions = multiClusterOptions.Value;
            this.serviceProvider = serviceProvider;
            this.logger = logger;
        }

        internal async Task<List<IGossipChannel>> CreateGossipChannels()
        {
            List<IGossipChannel> gossipChannels = new List<IGossipChannel>();

            foreach(KeyValuePair<string,string> channelConfig in this.multiClusterOptions.GossipChannels)
            {
                if (!string.IsNullOrWhiteSpace(channelConfig.Key))
                {
                    logger.Info("Creating Gossip Channel.");
                    switch (channelConfig.Key)
                    {
                        case MultiClusterOptions.BuiltIn.AzureTable:
                            var tableChannel = AssemblyLoader.LoadAndCreateInstance<IGossipChannel>(Constants.ORLEANS_CLUSTERING_AZURESTORAGE, logger, this.serviceProvider);
                            await tableChannel.Initialize(this.clusterOptions.ServiceId, channelConfig.Value);
                            gossipChannels.Add(tableChannel);
                            break;

                        default:
                            break;
                    }

                    logger.Info("Configured Gossip Channel: Type={0}", channelConfig.Key);
                }
            }

            return gossipChannels;
        }
    }
}
