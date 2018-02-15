using System;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal class ClientStatisticsManager : IDisposable
    {
        private readonly ClientStatisticsOptions statisticsOptions;
        private readonly LogStatistics logStatistics;

        public ClientStatisticsManager(
            SerializationManager serializationManager, 
            ILoggerFactory loggerFactory, 
            IOptions<ClientStatisticsOptions> statisticsOptions)
        {
            this.statisticsOptions = statisticsOptions.Value;
            this.logStatistics = new LogStatistics(this.statisticsOptions.LogWriteInterval, false, serializationManager, loggerFactory);
            MessagingStatisticsGroup.Init(false);
            NetworkingStatisticsGroup.Init(false);
            ApplicationRequestsStatisticsGroup.Init();
        }

        internal void Start(IMessageCenter transport, GrainId clientId)
        {
            this.logStatistics.Start();
        }

        internal void Stop()
        {
            this.logStatistics.Stop();
            this.logStatistics.DumpCounters();
        }

        public void Dispose()
        {
            this.logStatistics.Dispose();
        }
    }
}
