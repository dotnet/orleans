using System;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Messaging
{
    internal class GatewayProviderFactory
    {
        private readonly ClientConfiguration cfg;
        private readonly IServiceProvider serviceProvider;
        private readonly Logger logger;

        public GatewayProviderFactory(ClientConfiguration cfg, IServiceProvider serviceProvider)
        {
            this.cfg = cfg;
            this.serviceProvider = serviceProvider;
            this.logger = LogManager.GetLogger(typeof(GatewayProviderFactory).Name, LoggerType.Runtime);
        }

        internal IGatewayListProvider CreateGatewayListProvider()
        {
            IGatewayListProvider listProvider;
            ClientConfiguration.GatewayProviderType gatewayProviderToUse = cfg.GatewayProviderToUse;
            
            switch (gatewayProviderToUse)
            {
                case ClientConfiguration.GatewayProviderType.AzureTable:
                    listProvider = AssemblyLoader.LoadAndCreateInstance<IGatewayListProvider>(Constants.ORLEANS_AZURE_UTILS_DLL, logger, this.serviceProvider);
                    break;

                case ClientConfiguration.GatewayProviderType.SqlServer:
                    listProvider = AssemblyLoader.LoadAndCreateInstance<IGatewayListProvider>(Constants.ORLEANS_SQL_UTILS_DLL, logger, this.serviceProvider);
                    break;

                case ClientConfiguration.GatewayProviderType.Custom:
                    listProvider = AssemblyLoader.LoadAndCreateInstance<IGatewayListProvider>(cfg.CustomGatewayProviderAssemblyName, logger, this.serviceProvider);
                    break;

                case ClientConfiguration.GatewayProviderType.ZooKeeper:
                    listProvider = AssemblyLoader.LoadAndCreateInstance<IGatewayListProvider>(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL, logger, this.serviceProvider);
                    break;

                case ClientConfiguration.GatewayProviderType.Config:
                    listProvider = new StaticGatewayListProvider();
                    break;

                default:
                    throw new NotImplementedException(gatewayProviderToUse.ToString());
            }

            return listProvider;
        }
    }
}
