using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Core;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tester.StreamingTests
{
    public class QueueBalancerFactory : IStreamQueueBalancerFactory
    {
        public IStreamQueueBalancer Create(
            string strProviderName,
            ISiloStatusOracle siloStatusOracle,
            ClusterConfiguration clusterConfiguration,
            IProviderRuntime runtime,
            IStreamQueueMapper queueMapper,
            TimeSpan siloMaturityPeriod)
        {
            var keyedStreamQueueMapperCollection = runtime.ServiceProvider.GetService<IKeyedServiceCollection<string, IStreamQueueMapper>>() as KeyedStreamQueueMapperCollection;
            keyedStreamQueueMapperCollection.AddService(strProviderName, queueMapper);
            return new QueueBalancer(runtime.GrainFactory.GetGrain<ILeaseManagerGrain>(strProviderName), $"{strProviderName}-{Guid.NewGuid()}");
        }
    }

    public class KeyedStreamQueueMapperCollection : IKeyedServiceCollection<string, IStreamQueueMapper>
    {
        private ConcurrentDictionary<string, IStreamQueueMapper> collection = new ConcurrentDictionary<string, IStreamQueueMapper>();
        public IStreamQueueMapper GetService(string key)
        {
            IStreamQueueMapper mapper;
            this.collection.TryGetValue(key, out mapper);
            return mapper;
        }

        public void AddService(string key, IStreamQueueMapper fac)
        {
            this.collection.TryAdd(key, fac);
        }
    }

    public class KeyedQueueBalancerFactoryCollection : IKeyedServiceCollection<string, IStreamQueueBalancerFactory>
    {
        private Dictionary<string, IStreamQueueBalancerFactory> collection = new Dictionary<string, IStreamQueueBalancerFactory>();
        public IStreamQueueBalancerFactory GetService(string key)
        {
            IStreamQueueBalancerFactory factory;
            this.collection.TryGetValue(key, out factory);
            return factory;
        }

        public void AddService(string key, IStreamQueueBalancerFactory fac)
        {
            this.collection.Add(key, fac);
        }
    }
}
