using System;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal class ClientStatisticsManager : IDisposable
    {
        private readonly LogStatistics logStatistics;

        public ClientStatisticsManager(
            SerializationStatisticsGroup serializationStatistics, 
            ILoggerFactory loggerFactory, 
            IOptions<StatisticsOptions> statisticsOptions)
        {
            this.logStatistics = new LogStatistics(statisticsOptions.Value.LogWriteInterval, false, serializationStatistics, loggerFactory);
            MessagingStatisticsGroup.Init();
            NetworkingStatisticsGroup.Init();
        }

        internal void Start(IMessageCenter transport, GrainId clientId)
        {
            this.logStatistics.Start();
        }

        public void Dump()
        {
            this.logStatistics.DumpCounters();
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
