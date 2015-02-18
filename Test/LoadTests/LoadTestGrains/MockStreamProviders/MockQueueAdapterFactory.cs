using System;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams;
using OrleansProviders.PersistentStream.MockQueueAdapter;

namespace LoadTestGrains.MockStreamProviders
{
    public class MockQueueAdapterFactory : IQueueAdapterFactory
    {
        private MockStreamProviderSettings _settings;
        private string _providerName;
        private Logger _logger;

        public virtual void Init(IProviderConfiguration config, string providerName, Logger logger)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }

            _settings = new MockStreamProviderSettings(config.Properties);
            _providerName = providerName;
            _logger = logger;
        }

        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            IMockQueueAdapterMonitor monitor = new MockQueueAdapterMonitor(_settings, _logger);
            var adapter = new MockQueueAdapter(_providerName, _settings, () => new MockQueueAdapterGenerator(_settings, _logger), monitor);
            return Task.FromResult<IQueueAdapter>(adapter);
        }
    }
}