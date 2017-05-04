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

namespace UnitTests.Stats
{
    internal interface IMockClientStatisticsPublisher : IStatisticsPublisher
    {
        long NumStatsCalls { get; }
    }
    internal interface IMockClientMetricsPublisher : IClientMetricsDataPublisher
    {
        long NumMetricsCalls { get; }
    }

    /// <summary>
    /// Indirection for a test hook to client stats / metrics collector singleton instances
    /// </summary>
    internal static class MockStatsCollectorClient
    {
        internal static IMockClientStatisticsPublisher StatsPublisherInstance;
        internal static IMockClientMetricsPublisher MetricsPublisherInstance;
    }

    public class MockStatsClientCollector : MarshalByRefObject,
        IMockClientStatisticsPublisher, IMockClientMetricsPublisher, // Stats providers have to be both Stats and Metrics publishers
        IProvider // Needs to be IProvider as well as *Publisher
    {
        public string Name { get; private set; }
        public long NumStatsCalls { get { return numStatsCalls; } }
        public long NumMetricsCalls { get { return numMetricsCalls; } }

        private long numStatsCalls;
        private long numMetricsCalls;

        public MockStatsClientCollector()
        {
            Trace.TraceInformation("{0} created", GetType().FullName);
            numStatsCalls = 0;
            numMetricsCalls = 0;
            MockStatsCollectorClient.MetricsPublisherInstance = this;
            MockStatsCollectorClient.StatsPublisherInstance = this;
        }
        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Trace.TraceInformation("{0} Init called", GetType().Name);
            Name = name;
            return Task.CompletedTask;
        }

        public Task Init(ClientConfiguration config, IPAddress address, string clientId)
        {
            throw new NotImplementedException();
        }

        public Task ReportMetrics(IClientPerformanceMetrics metricsData)
        {
            Trace.TraceInformation("{0} ReportMetrics called", GetType().Name);
            Interlocked.Increment(ref numMetricsCalls);
            return Task.CompletedTask;
        }
        public Task ReportStats(List<ICounter> statsCounters)
        {
            Trace.TraceInformation("{0} ReportStats called", GetType().Name);
            Interlocked.Increment(ref numStatsCalls);
            return Task.CompletedTask;
        }

        public Task Init(bool isSilo, string storageConnectionString, string deploymentId, string address, string siloName,
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
        IStatisticsPublisher, ISiloMetricsDataPublisher, // Stats providers have to be both Stats and Metrics publishers
        IProvider // Needs to be IProvider as well as *Publisher
    {
        public string Name { get; private set; }

        private IStatsCollectorGrain grain;
        private OrleansTaskScheduler taskScheduler;
        private SchedulingContext schedulingContext;
        private Logger logger;

        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            this.logger = providerRuntime.GetLogger("MockStatsSiloCollector");
            this.grain = providerRuntime.GrainFactory.GetGrain<IStatsCollectorGrain>(0);
            this.taskScheduler = providerRuntime.ServiceProvider.GetRequiredService<OrleansTaskScheduler>();
            this.schedulingContext = providerRuntime.ServiceProvider.GetRequiredService<Silo>().testHook.SchedulingContext;
            logger.Info("{0} Init called", GetType().Name);
            return Task.CompletedTask;
        }

        public Task Init(string deploymentId, string storageConnectionString, SiloAddress siloAddress, string siloName,
            IPEndPoint gateway, string hostName)
        {
            throw new NotImplementedException();
        }

        public Task ReportMetrics(ISiloPerformanceMetrics metricsData)
        {
            logger.Info("{0} ReportMetrics called", GetType().Name);
            taskScheduler.QueueTask(() => grain.ReportMetricsCalled(), schedulingContext).Ignore();
            return Task.CompletedTask;
        }
        public Task ReportStats(List<ICounter> statsCounters)
        {
            logger.Info("{0} ReportStats called", GetType().Name);
            taskScheduler.QueueTask(() => grain.ReportStatsCalled(), schedulingContext).Ignore();
            return Task.CompletedTask;
        }

        public Task Init(bool isSilo, string storageConnectionString, string deploymentId, string address, string siloName,
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
