using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MultiClusterNetwork
{
    internal class MultiClusterGossipChannelFactory
    {
        private readonly SiloOptions siloOptions;
        private readonly GlobalConfiguration globalConfig;
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;

        public MultiClusterGossipChannelFactory(IOptions<SiloOptions> siloOptions, GlobalConfiguration globalConfig, IServiceProvider serviceProvider, ILogger<MultiClusterGossipChannelFactory> logger)
        {
            this.siloOptions = siloOptions.Value;
            this.globalConfig = globalConfig;
            this.serviceProvider = serviceProvider;
            this.logger = logger;
        }

        internal async Task<List<IGossipChannel>> CreateGossipChannels()
        {
            List<IGossipChannel> gossipChannels = new List<IGossipChannel>();

            var channelConfigurations = this.globalConfig.GossipChannels;
            if (channelConfigurations != null)
            {
                logger.Info("Creating Gossip Channels.");
                foreach (var channelConfiguration in channelConfigurations)
                {
                    switch (channelConfiguration.ChannelType)
                    {
                        case GlobalConfiguration.GossipChannelType.AzureTable:
                            var tableChannel = AssemblyLoader.LoadAndCreateInstance<IGossipChannel>(Constants.ORLEANS_CLUSTERING_AZURESTORAGE, logger, this.serviceProvider);
                            await tableChannel.Initialize(this.siloOptions.ServiceId, channelConfiguration.ConnectionString);
                            gossipChannels.Add(tableChannel);
                            break;

                        default:
                            break;
                    }

                    logger.Info("Configured Gossip Channel: Type={0} ConnectionString={1}", channelConfiguration.ChannelType, channelConfiguration.ConnectionString);
                }
            }

            return gossipChannels;
        }
    }
}
