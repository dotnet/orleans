using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Providers.AdoNet;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost.Utils;
using Orleans.AdoNet.Configuration;
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
        
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly AdoNetStatisticsPublisher StatisticsPublisher;
        
        protected SqlStatisticsPublisherTestsBase(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
        {
            this.environment = environment;
            this.loggerFactory = TestingUtils.CreateDefaultLoggerFactory($"{this.GetType()}.log");
            logger = loggerFactory.CreateLogger<SqlStatisticsPublisherTestsBase>();

            fixture.InitializeConnectionStringAccessor(GetConnectionString);

            ConnectionString = fixture.ConnectionString;

            StatisticsPublisher = new AdoNetStatisticsPublisher();
            StatisticsPublisher.Init("Test", new StatisticsPublisherProviderRuntime(),
                new StatisticsPublisherProviderConfig(AdoInvariant, ConnectionString)).Wait();
        }

        protected async Task<string> GetConnectionString()
        {
            var instance = await RelationalStorageForTesting.SetupInstance(this.AdoInvariant, testDatabaseName);
            return instance.CurrentConnectionString;
        }
        
        protected async Task SqlStatisticsPublisher_ReportStats()
        {
            StatisticsPublisher.AddConfiguration("statisticsDeployment", "statisticsHostName", "statisticsClient", IPAddress.Loopback);
            await RunParallel(10, () => StatisticsPublisher.ReportStats(new List<ICounter> { new DummyCounter(),new DummyCounter() }));
        }
        
        private Task RunParallel(int count, Func<Task> taskFactory)
        {
            return Task.WhenAll(Enumerable.Range(0, count).Select(x => taskFactory()));
        }
    }
}