using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MultiClusterNetwork
{
    internal class MultiClusterOracleFactory
    {
        private readonly Logger logger;

        internal MultiClusterOracleFactory()
        {
            logger = LogManager.GetLogger("MultiClusterOracleFactory", LoggerType.Runtime);
        }

        internal async Task<IMultiClusterOracle> CreateGossipOracle(Silo silo)
        {
            if (! silo.GlobalConfig.HasMultiClusterNetwork)
            {
                logger.Info("Skip multicluster oracle creation (no multicluster network configured)");
                return null;
            }      
             
            logger.Info("Creating multicluster oracle...");

            var channels = await GetGossipChannels(silo);

            if (channels.Count == 0)
                logger.Warn(ErrorCode.MultiClusterNetwork_NoChannelsConfigured, "No gossip channels are configured.");

            var gossipOracle = new MultiClusterOracle(silo.SiloAddress, channels, silo.GlobalConfig);

            logger.Info("Created multicluster oracle.");

            return gossipOracle;
        }

        internal async Task<List<IGossipChannel>> GetGossipChannels(Silo silo)
        {
            List<IGossipChannel> gossipChannels = new List<IGossipChannel>();

            var channelConfigurations = silo.GlobalConfig.GossipChannels;
            if (channelConfigurations != null)
            {
                foreach (var channelConfiguration in channelConfigurations)
                {
                    switch (channelConfiguration.ChannelType)
                    {
                        case GlobalConfiguration.GossipChannelType.AzureTable:
                            var tableChannel = AssemblyLoader.LoadAndCreateInstance<IGossipChannel>(Constants.ORLEANS_AZURE_UTILS_DLL, logger);
                            await tableChannel.Initialize(silo.GlobalConfig.ServiceId, channelConfiguration.ConnectionString);
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
