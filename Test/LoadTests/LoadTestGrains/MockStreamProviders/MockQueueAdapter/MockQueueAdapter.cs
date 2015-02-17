using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Streams;

namespace OrleansProviders.PersistentStream.MockQueueAdapter
{
    public class MockQueueAdapter : IQueueAdapter
    {
        private readonly IStreamQueueMapper _streamQueueMapper;
        private readonly IMockQueueAdapterSettings _settings;
        private readonly Func<IMockQueueAdapterBatchGenerator> _generatorFactory;
        private readonly IMockQueueAdapterMonitor _monitor;

        public string Name { get; private set; }

        public bool IsRewindable { get { return true; } }

        public StreamProviderDirection Direction { get { return StreamProviderDirection.ReadWrite; } }


        public MockQueueAdapter(string providerName, IMockQueueAdapterSettings settings, Func<IMockQueueAdapterBatchGenerator> generatorFactory, IMockQueueAdapterMonitor monitor)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            if (generatorFactory == null)
            {
                throw new ArgumentNullException("generatorFactory");
            }
            if (monitor == null)
            {
                throw new ArgumentNullException("monitor");
            }

            Name = providerName;
            _settings = settings;
            _generatorFactory = generatorFactory;
            _monitor = monitor;
            //_streamQueueMapper = new FixedRingStreamQueueMapper(settings.TotalQueueCount, "MockQueueAdapter", new MockFixedRingConfig(_settings.NumPullingAgents));
            _streamQueueMapper = new HashRingBasedStreamQueueMapper(settings.TotalQueueCount, "MockQueueAdapter");

            _monitor.AdapterCreated();
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return MockQueueAdapterReceiver.Create(queueId, _settings, _generatorFactory(), _monitor);
        }

        public IStreamQueueMapper GetStreamQueueMapper()
        {
            return _streamQueueMapper;
        }

        public Task QueueMessageBatchAsync<T>(Guid streamGuid, String streamNamespace, IEnumerable<T> events)
        {
            throw new NotSupportedException("MockQueueAdapter doesn't support enqueueing messages yet.");
        }

        ///// <summary>
        ///// Mock fixed ring config.
        ///// Since we don't know how many silos there are, and we don't know which silo we are, and the queues are not 
        ///// tied to any real storage we just treat each adapter like it's the only one and use a per silo queue count.
        ///// </summary>
        //private class MockFixedRingConfig : IFixedRingConfig
        //{
        //    public int SiloIndex { get { return 0; } }
        //    public int SiloCount { get { return 1; } }
        //    public int AgentPerSiloCount { get; private set; }

        //    public MockFixedRingConfig(int numPullingAgents)
        //    {
        //        AgentPerSiloCount = numPullingAgents;
        //    }
        //}
    }
}