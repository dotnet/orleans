using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MultiClusterNetwork
{
    internal class MultiClusterGossipChannelFactory
    {
        private readonly GlobalConfiguration globalConfig;
        private readonly Logger logger;

        public MultiClusterGossipChannelFactory(GlobalConfiguration globalConfig)
        {
            this.globalConfig = globalConfig;
            logger = LogManager.GetLogger("MultiClusterGossipChannelFactory", LoggerType.Runtime);
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
                            var tableChannel = AssemblyLoader.LoadAndCreateInstance<IGossipChannel>(Constants.ORLEANS_AZURE_UTILS_DLL, logger);
                            await tableChannel.Initialize(globalConfig.ServiceId, channelConfiguration.ConnectionString);
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
