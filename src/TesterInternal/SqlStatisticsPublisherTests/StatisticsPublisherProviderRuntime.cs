using System;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;

namespace UnitTests.SqlStatisticsPublisherTests
{
    internal class StatisticsPublisherProviderRuntime : IProviderRuntime
    {
        private readonly Logger logger;

        public StatisticsPublisherProviderRuntime(Logger logger)
        {
            this.logger = logger;
        }

        public Logger GetLogger(string loggerName)
        {
            return logger;
        }

        public Guid ServiceId
        {
            get { throw new NotImplementedException(); }
        }

        public string SiloIdentity
        {
            get { throw new NotImplementedException(); }
        }

        public IGrainFactory GrainFactory
        {
            get { throw new NotImplementedException(); }
        }

        public IServiceProvider ServiceProvider
        {
            get { throw new NotImplementedException(); }
        }
    }
}
