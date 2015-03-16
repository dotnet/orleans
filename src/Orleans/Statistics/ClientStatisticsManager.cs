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

﻿using System;
using System.Threading.Tasks;
using Orleans.AzureUtils;
using Orleans.Runtime.Configuration;
using Orleans.Providers;

namespace Orleans.Runtime
{
    internal class ClientStatisticsManager
    {
        private ClientTableStatistics tableStatistics;
        private LogStatistics logStatistics;
        private RuntimeStatisticsGroup runtimeStats;

        internal ClientStatisticsManager(IStatisticsConfiguration config)
        {
            runtimeStats = new RuntimeStatisticsGroup();
            logStatistics = new LogStatistics(config.StatisticsLogWriteInterval, false);
        }

        internal async Task Start(ClientConfiguration config, StatisticsProviderManager statsManager, IMessageCenter transport, GrainId clientId)
        {
            MessagingStatisticsGroup.Init(false);
            NetworkingStatisticsGroup.Init(false);
            ApplicationRequestsStatisticsGroup.Init(config.ResponseTimeout);

            runtimeStats.Start();

            // Configure Metrics
            IProvider statsProvider = null;
            if (!string.IsNullOrEmpty(config.StatisticsProviderName))
            {
                var extType = config.StatisticsProviderName;
                statsProvider = statsManager.GetProvider(extType);
                var metricsDataPublisher = statsProvider as IClientMetricsDataPublisher;
                if (metricsDataPublisher == null)
                {
                    var msg = String.Format("Trying to create {0} as a metrics publisher, but the provider is not configured."
                        , extType);
                    throw new ArgumentException(msg, "ProviderType (configuration)");
                }
                var configurableMetricsDataPublisher = metricsDataPublisher as IConfigurableClientMetricsDataPublisher;
                if (configurableMetricsDataPublisher != null)
                {
                    configurableMetricsDataPublisher.AddConfiguration(
                        config.DeploymentId, config.DNSHostName, clientId.ToString(), transport.MyAddress.Endpoint.Address);
                }
                tableStatistics = new ClientTableStatistics(transport, metricsDataPublisher, runtimeStats)
                {
                    MetricsTableWriteInterval = config.StatisticsMetricsTableWriteInterval
                };
            }
            else if (config.UseAzureSystemStore)
            {
                // Hook up to publish client metrics to Azure storage table
                var publisher = await ClientMetricsTableDataManager.GetManager(config, transport.MyAddress.Endpoint.Address, clientId);
                tableStatistics = new ClientTableStatistics(transport, publisher, runtimeStats)
                {
                    MetricsTableWriteInterval = config.StatisticsMetricsTableWriteInterval
                };
            }

            // Configure Statistics
            if (config.StatisticsWriteLogStatisticsToTable)
            {
                if (statsProvider != null)
                {
                    logStatistics.StatsTablePublisher = statsProvider as IStatisticsPublisher;
                    // Note: Provider has already been Init-ialized above.
                }
                else if (config.UseAzureSystemStore)
                {
                    var statsDataPublisher = await StatsTableDataManager.GetManager(false, config.DataConnectionString, config.DeploymentId,
                        transport.MyAddress.Endpoint.ToString(), clientId.ToParsableString(), config.DNSHostName);
                    logStatistics.StatsTablePublisher = statsDataPublisher;
                }
            }
            logStatistics.Start();
        }

        internal void Stop()
        {
            if (runtimeStats != null)
                runtimeStats.Stop();

            runtimeStats = null;

            if (logStatistics != null)
            {
                logStatistics.Stop();
                logStatistics.DumpCounters().WaitWithThrow(TimeSpan.FromSeconds(10));
            }

            logStatistics = null;

            if (tableStatistics != null)
                tableStatistics.Dispose();

            tableStatistics = null;
        }
    }
}
