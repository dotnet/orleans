/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
        private List<Uri> knownGateways;


        #region Implementation of IGatewayListProvider
        
        public Task InitializeGatewayListProvider(ClientConfiguration cfg, TraceLogger traceLogger)
        {
            knownGateways = cfg.Gateways.Select(ep => ep.ToGatewayUri()).ToList();
            return TaskDone.Done; ;
        }

        public IList<Uri> GetGateways()
        {
            return knownGateways;
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