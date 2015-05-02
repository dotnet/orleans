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
using System.Threading.Tasks;

using Orleans.Runtime.Configuration;
using Orleans.AzureUtils;

namespace Orleans.Runtime.Counters
{
    internal class SiloStatisticsManager
    {
        private LogStatistics logStatistics;
        private RuntimeStatisticsGroup runtimeStats;
        private PerfCountersStatistics perfCountersPublisher;
        internal SiloPerformanceMetrics MetricsTable;

        internal SiloStatisticsManager(GlobalConfiguration globalConfig, NodeConfiguration nodeConfig)
        {
            MessagingStatisticsGroup.Init(true);
            MessagingProcessingStatisticsGroup.Init();
            NetworkingStatisticsGroup.Init(true);
            ApplicationRequestsStatisticsGroup.Init(globalConfig.ResponseTimeout);
            SchedulerStatisticsGroup.Init();
            StorageStatisticsGroup.Init();
            runtimeStats = new RuntimeStatisticsGroup();
            logStatistics = new LogStatistics(nodeConfig.StatisticsLogWriteInterval, true);
            MetricsTable = new SiloPerformanceMetrics(runtimeStats, nodeConfig);
            perfCountersPublisher = new PerfCountersStatistics(nodeConfig.StatisticsPerfCountersWriteInterval);
        }

        internal async Task SetSiloMetricsTableDataManager(Silo silo, NodeConfiguration nodeConfig)
        {
            bool useAzureTable;
            bool useExternalMetricsProvider = ShouldUseExternalMetricsProvider(silo, nodeConfig, out useAzureTable);

            if (useExternalMetricsProvider)
            {
                var extType = nodeConfig.StatisticsProviderName;
                var metricsProvider = silo.StatisticsProviderManager.GetProvider(extType);
                var metricsDataPublisher = metricsProvider as ISiloMetricsDataPublisher;
                if (metricsDataPublisher == null)
                {
                    var msg = String.Format("Trying to create {0} as a silo metrics publisher, but the provider is not available."
                        + " Expected type = {1} Actual type = {2}",
                        extType, typeof(IStatisticsPublisher), metricsProvider.GetType());
                    throw new InvalidOperationException(msg);
                }
                var configurableMetricsDataPublisher = metricsDataPublisher as IConfigurableSiloMetricsDataPublisher;
                if (configurableMetricsDataPublisher != null)
                {
                    var gateway = nodeConfig.IsGatewayNode ? nodeConfig.ProxyGatewayEndpoint : null;
                    configurableMetricsDataPublisher.AddConfiguration(
                        silo.GlobalConfig.DeploymentId, true, silo.Name, silo.SiloAddress, gateway, nodeConfig.DNSHostName);
                }
                MetricsTable.MetricsDataPublisher = metricsDataPublisher;
            }
            else if (useAzureTable)
            {
                // Hook up to publish silo metrics to Azure storage table
                var gateway = nodeConfig.IsGatewayNode ? nodeConfig.ProxyGatewayEndpoint : null;
                var metricsDataPublisher = await SiloMetricsTableDataManager.GetManager(silo.GlobalConfig.DeploymentId, silo.GlobalConfig.DataConnectionString, silo.SiloAddress, silo.Name, gateway, nodeConfig.DNSHostName);
                MetricsTable.MetricsDataPublisher = metricsDataPublisher;
            }
            // else no metrics
        }

        internal async Task SetSiloStatsTableDataManager(Silo silo, NodeConfiguration nodeConfig)
        {
            bool useAzureTable;
            bool useExternalStatsProvider = ShouldUseExternalMetricsProvider(silo, nodeConfig, out useAzureTable);

            if (!nodeConfig.StatisticsWriteLogStatisticsToTable) return; // No stats

            if (useExternalStatsProvider)
            {
                var extType = nodeConfig.StatisticsProviderName;
                var statsProvider = silo.StatisticsProviderManager.GetProvider(extType);
                var statsDataPublisher = statsProvider as IStatisticsPublisher;
                if (statsDataPublisher == null)
                {
                    var msg = String.Format("Trying to create {0} as a silo statistics publisher, but the provider is not available."
                        + " Expected type = {1} Actual type = {2}",
                        extType, typeof(IStatisticsPublisher), statsProvider.GetType());
                    throw new InvalidOperationException(msg);
                }
                var configurableStatsDataPublisher = statsDataPublisher as IConfigurableStatisticsPublisher;
                if (configurableStatsDataPublisher != null)
                {
                    var gateway = nodeConfig.IsGatewayNode ? nodeConfig.ProxyGatewayEndpoint : null;
                    configurableStatsDataPublisher.AddConfiguration(
                        silo.GlobalConfig.DeploymentId, true, silo.Name, silo.SiloAddress, gateway, nodeConfig.DNSHostName);
                }
                logStatistics.StatsTablePublisher = statsDataPublisher;
            }
            else if (useAzureTable)
            {
                var statsDataPublisher = await StatsTableDataManager.GetManager(true, silo.GlobalConfig.DataConnectionString, silo.GlobalConfig.DeploymentId, silo.SiloAddress.ToLongString(), silo.Name, nodeConfig.DNSHostName);
                logStatistics.StatsTablePublisher = statsDataPublisher;
            }
            // else no stats
        }

        private static bool ShouldUseExternalMetricsProvider(
            Silo silo,
            IStatisticsConfiguration nodeConfig,
            out bool useAzureTable)
        {
            useAzureTable = silo.GlobalConfig.LivenessType == GlobalConfiguration.LivenessProviderType.AzureTable
                                 && !string.IsNullOrEmpty(silo.GlobalConfig.DeploymentId)
                                 && !string.IsNullOrEmpty(silo.GlobalConfig.DataConnectionString);

            return !string.IsNullOrEmpty(nodeConfig.StatisticsProviderName);
        }

        internal void Start(NodeConfiguration config)
        {
            perfCountersPublisher.Start();
            logStatistics.Start();
            runtimeStats.Start();
            // Start performance metrics publisher
            MetricsTable.MetricsTableWriteInterval = config.StatisticsMetricsTableWriteInterval;
        }

        internal void Stop()
        {
            if (runtimeStats != null)
                runtimeStats.Stop();
            runtimeStats = null;
            if (MetricsTable != null)
                MetricsTable.Dispose();
            MetricsTable = null;
            if (perfCountersPublisher != null)
                perfCountersPublisher.Stop();
            perfCountersPublisher = null;
            if (logStatistics != null)
            {
                logStatistics.Stop();
                logStatistics.DumpCounters().Wait();
            }
            logStatistics = null;
        }
    }
}
