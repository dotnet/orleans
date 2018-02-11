using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.TestHooks;

namespace UnitTests.Stats
{
    internal interface IMockClientStatisticsPublisher : IStatisticsPublisher
    {
        long NumStatsCalls { get; }
    }

    /// <summary>
    /// Indirection for a test hook to client stats / metrics collector singleton instances
    /// </summary>
    internal static class MockStatsCollectorClient
    {
        internal static IMockClientStatisticsPublisher StatsPublisherInstance;
    }

    public class MockStatsClientCollector : MarshalByRefObject,
        IMockClientStatisticsPublisher,
        IProvider // Needs to be IProvider as well as *Publisher
    {
        public string Name { get; private set; }
        public long NumStatsCalls { get { return numStatsCalls; } }

        private long numStatsCalls;

        public MockStatsClientCollector()
        {
            Trace.TraceInformation("{0} created", GetType().FullName);
            numStatsCalls = 0;
            MockStatsCollectorClient.StatsPublisherInstance = this;
        }
        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Trace.TraceInformation("{0} Init called", GetType().Name);
            Name = name;
            return Task.CompletedTask;
        }

        public Task Init(IPAddress address, string clientId)
        {
            throw new NotImplementedException();
        }
        
        public Task ReportStats(List<ICounter> statsCounters)
        {
            Trace.TraceInformation("{0} ReportStats called", GetType().Name);
            Interlocked.Increment(ref numStatsCalls);
            return Task.CompletedTask;
        }

        public Task Init(bool isSilo, string storageConnectionString, string clusterId, string address, string siloName,
            string hostName)
        {
            return Task.CompletedTask;
        }

        public Task Close()
        {
            return Task.CompletedTask;
        }
    }

    public class MockStatsSiloCollector :
        IStatisticsPublisher,
        IProvider // Needs to be IProvider as well as *Publisher
    {
        public string Name { get; private set; }

        private IStatsCollectorGrain grain;
        private OrleansTaskScheduler taskScheduler;
        private SchedulingContext schedulingContext;
        private ILogger logger;

        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            this.logger = providerRuntime.ServiceProvider.GetRequiredService<ILogger<MockStatsSiloCollector>>();
            this.grain = providerRuntime.GrainFactory.GetGrain<IStatsCollectorGrain>(0);
            this.taskScheduler = providerRuntime.ServiceProvider.GetRequiredService<OrleansTaskScheduler>();
            this.schedulingContext = providerRuntime.ServiceProvider.GetRequiredService<TestHooksSystemTarget>().SchedulingContext;
            logger.Info("{0} Init called", GetType().Name);
            return Task.CompletedTask;
        }

        public Task Init(string clusterId, string storageConnectionString, SiloAddress siloAddress, string siloName,
            IPEndPoint gateway, string hostName)
        {
            throw new NotImplementedException();
        }

        public Task ReportStats(List<ICounter> statsCounters)
        {
            logger.Info("{0} ReportStats called", GetType().Name);
            taskScheduler.QueueTask(() => grain.ReportStatsCalled(), schedulingContext).Ignore();
            return Task.CompletedTask;
        }

        public Task Init(bool isSilo, string storageConnectionString, string clusterId, string address, string siloName,
            string hostName)
        {
            return Task.CompletedTask;
        }

        public Task Close()
        {
            return Task.CompletedTask;
        }
    }
}
