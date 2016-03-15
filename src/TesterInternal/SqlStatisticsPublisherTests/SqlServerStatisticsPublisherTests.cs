using System;
using Orleans.Providers.SqlServer;
using UnitTests.General;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.SqlUtils;
using UnitTests.SqlStatisticsPublisherTests;
using Xunit;

namespace UnitTests.SqlStatisticsTest
{
    /// <summary>
    /// Tests for operation of Orleans Statistics Publisher using SQL Server
    /// </summary>
    public class SqlServerStatisticsPublisherTests : IClassFixture<SqlServerStatisticsPublisherTests.Fixture>
    {
        public class Fixture
        {
            public readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
            public string ConnectionString;
            private const string testDatabaseName = "OrleansTest";
            public string AdoInvariant = AdoNetInvariants.InvariantNameSqlServer;


            public readonly TraceLogger Logger = TraceLogger.GetLogger("MySqlStatisticsPublisherTests",
                TraceLogger.LoggerType.Application);

            internal SqlStatisticsPublisher StatisticsPublisher;


            // Use ClassInitialize to run code before running the first test in the class
            public Fixture()
            {
                TraceLogger.Initialize(new NodeConfiguration());
                TraceLogger.AddTraceLevelOverride("SqlStatisticsPublisherTests", Severity.Verbose3);

                ConnectionString =
                    RelationalStorageForTesting.SetupInstance(AdoInvariant, testDatabaseName)
                        .Result.CurrentConnectionString;
            }

            public async Task Initialize()
            {
                StatisticsPublisher = new SqlStatisticsPublisher();
                await StatisticsPublisher.Init("Test", new StatisticsPublisherProviderRuntime(Logger),
                    new StatisticsPublisherProviderConfig(AdoInvariant, ConnectionString));
            }
        }

        private const string dbName = "SqlServer";
        private Fixture _fixture;

        public SqlServerStatisticsPublisherTests(Fixture fixture)
        {
            _fixture = fixture;
        }        
      
        [Fact, TestCategory("Statistics"), TestCategory(dbName)]
        public async Task SqlStatisticsPublisher_SqlServer_Init()
        {
            await _fixture.Initialize();
            Assert.IsNotNull(_fixture.StatisticsPublisher, "Statistics publisher created");
        }


        [Fact, TestCategory("Statistics"), TestCategory(dbName)]
        public async Task SqlStatisticsPublisher_SqlServer_ReportMetrics_Client()
        {
            await _fixture.Initialize();
            _fixture.StatisticsPublisher.AddConfiguration("statisticsDeployment", "statisticsHostName", "statisticsClient", IPAddress.Loopback);
            await RunParallel(10, () => _fixture.StatisticsPublisher.ReportMetrics((IClientPerformanceMetrics)new DummyPerformanceMetrics()));
        }

        [Fact, TestCategory("Statistics"), TestCategory(dbName)]
        public async Task SqlStatisticsPublisher_SqlServer_ReportStats()
        {
            await _fixture.Initialize();
            _fixture.StatisticsPublisher.AddConfiguration("statisticsDeployment", "statisticsHostName", "statisticsClient", IPAddress.Loopback);
            await RunParallel(10, () => _fixture.StatisticsPublisher.ReportStats(new List<ICounter> { new DummyCounter() }));
        }

        [Fact, TestCategory("Statistics"), TestCategory(dbName)]
        public async Task SqlStatisticsPublisher_SqlServer_ReportMetrics_Silo()
        {
            GlobalConfiguration config = new GlobalConfiguration
            {
                DeploymentId = "statisticsDeployment",
                AdoInvariant = _fixture.AdoInvariant,
                DataConnectionString = _fixture.ConnectionString
            };

            IMembershipTable mbr = new SqlMembershipTable();
            await mbr.InitializeMembershipTable(config, true, _fixture.Logger).WithTimeout(_fixture.Timeout);
            await _fixture.Initialize();
            _fixture.StatisticsPublisher.AddConfiguration("statisticsDeployment", true, "statisticsSiloId", SiloAddress.NewLocalAddress(0), new IPEndPoint(IPAddress.Loopback, 12345), "statisticsHostName");
            await RunParallel(10, () => _fixture.StatisticsPublisher.ReportMetrics((ISiloPerformanceMetrics)new DummyPerformanceMetrics()));
        }

        private Task RunParallel(int count, Func<Task> taskFactory)
        {
            return Task.WhenAll(Enumerable.Range(0, count).Select(x => taskFactory()));
        }
    }
}
