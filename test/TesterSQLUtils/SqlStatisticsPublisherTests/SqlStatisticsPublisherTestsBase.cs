using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Providers.SqlServer;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using UnitTests.General;
using Xunit;

namespace UnitTests.SqlStatisticsPublisherTests
{
    /// <summary>
    /// Tests for operation of Orleans Statistics Publisher using relational storage
    /// </summary>
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public abstract class SqlStatisticsPublisherTestsBase: IClassFixture<ConnectionStringFixture>
    {
        private readonly TestEnvironmentFixture environment;
        protected abstract string AdoInvariant { get; }

        private readonly string ConnectionString;

        private const string testDatabaseName = "OrleansStatisticsTest";
        
        private readonly Logger logger;

        private readonly SqlStatisticsPublisher StatisticsPublisher;
        
        protected SqlStatisticsPublisherTestsBase(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
        {
            this.environment = environment;
            LogManager.Initialize(new NodeConfiguration());
            logger = LogManager.GetLogger(GetType().Name, LoggerType.Application);

            fixture.InitializeConnectionStringAccessor(GetConnectionString);

            ConnectionString = fixture.ConnectionString;

            StatisticsPublisher = new SqlStatisticsPublisher();
            StatisticsPublisher.Init("Test", new StatisticsPublisherProviderRuntime(logger),
                new StatisticsPublisherProviderConfig(AdoInvariant, ConnectionString)).Wait();
        }

        protected async Task<string> GetConnectionString()
        {
            var instance = await RelationalStorageForTesting.SetupInstance(this.AdoInvariant, testDatabaseName);
            return instance.CurrentConnectionString;
        }

        protected async Task SqlStatisticsPublisher_ReportMetrics_Client()
        {
            StatisticsPublisher.AddConfiguration("statisticsDeployment", "statisticsHostName", "statisticsClient", IPAddress.Loopback);
            await RunParallel(10, () => StatisticsPublisher.ReportMetrics((IClientPerformanceMetrics)new DummyPerformanceMetrics()));
        }

        protected async Task SqlStatisticsPublisher_ReportStats()
        {
            StatisticsPublisher.AddConfiguration("statisticsDeployment", "statisticsHostName", "statisticsClient", IPAddress.Loopback);
            await RunParallel(10, () => StatisticsPublisher.ReportStats(new List<ICounter> { new DummyCounter(),new DummyCounter() }));
        }

        protected async Task SqlStatisticsPublisher_ReportMetrics_Silo()
        {
            GlobalConfiguration config = new GlobalConfiguration
            {
                DeploymentId = "statisticsDeployment",
                AdoInvariant = AdoInvariant,
                DataConnectionString = ConnectionString
            };

            IMembershipTable mbr = new SqlMembershipTable(this.environment.Services.GetRequiredService<IGrainReferenceConverter>());
            await mbr.InitializeMembershipTable(config, true, logger).WithTimeout(TimeSpan.FromMinutes(1));
            StatisticsPublisher.AddConfiguration("statisticsDeployment", true, "statisticsSiloId", SiloAddress.NewLocalAddress(0), new IPEndPoint(IPAddress.Loopback, 12345), "statisticsHostName");
            await RunParallel(10, () => StatisticsPublisher.ReportMetrics((ISiloPerformanceMetrics)new DummyPerformanceMetrics()));
        }

        private Task RunParallel(int count, Func<Task> taskFactory)
        {
            return Task.WhenAll(Enumerable.Range(0, count).Select(x => taskFactory()));
        }
    }
}