using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Messaging
{
    internal class GatewayProviderFactory
    {
        private static readonly TraceLogger logger = TraceLogger.GetLogger(typeof(GatewayProviderFactory).Name, TraceLogger.LoggerType.Runtime);

        internal static async Task<IGatewayListProvider> CreateGatewayListProvider(ClientConfiguration cfg)
        {
            IGatewayListProvider listProvider = null;
            ClientConfiguration.GatewayProviderType gatewayProviderToUse = cfg.GatewayProviderToUse;

            switch (gatewayProviderToUse)
            {
                case ClientConfiguration.GatewayProviderType.AzureTable:
                    listProvider = AssemblyLoader.LoadAndCreateInstance<IGatewayListProvider>(Constants.ORLEANS_AZURE_UTILS_DLL, logger);
                    break;

                case ClientConfiguration.GatewayProviderType.SqlServer:
                    listProvider = AssemblyLoader.LoadAndCreateInstance<IGatewayListProvider>(Constants.ORLEANS_SQL_UTILS_DLL, logger);
                    break;

                case ClientConfiguration.GatewayProviderType.Custom:
                    listProvider = AssemblyLoader.LoadAndCreateInstance<IGatewayListProvider>(cfg.CustomGatewayProviderAssemblyName, logger);
                    break;

                case ClientConfiguration.GatewayProviderType.ZooKeeper:
                    listProvider = AssemblyLoader.LoadAndCreateInstance<IGatewayListProvider>(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL, logger);
                    break;

                case ClientConfiguration.GatewayProviderType.Config:
                    listProvider = new StaticGatewayListProvider();
                    break;

                default:
                    throw new NotImplementedException(gatewayProviderToUse.ToString());
            }

            await listProvider.InitializeGatewayListProvider(cfg, TraceLogger.GetLogger(listProvider.GetType().Name));
            return listProvider;
        }
    }


    internal class StaticGatewayListProvider : IGatewayListProvider
    {
        private IList<Uri> knownGateways;


        #region Implementation of IGatewayListProvider
        
        public Task InitializeGatewayListProvider(ClientConfiguration cfg, TraceLogger traceLogger)
        {
            knownGateways = cfg.Gateways.Select(ep => ep.ToGatewayUri()).ToList();
            return TaskDone.Done;
        }

        public Task<IList<Uri>> GetGateways()
        {
            return Task.FromResult(knownGateways);
        }

        public TimeSpan MaxStaleness 
        {
            get { return TimeSpan.MaxValue; }
        }

        public bool IsUpdatable
        {
            get { return false; }
        }

        #endregion
    }
}
