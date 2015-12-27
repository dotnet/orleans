using System;
using Orleans.Providers.SqlServer;
using UnitTests.General;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.SqlUtils;
using UnitTests.SqlStatisticsPublisherTests;

namespace UnitTests.SqlStatisticsTest
{
    /// <summary>
    /// Tests for operation of Orleans Statistics Publisher using SQL Server
    /// </summary>
    [TestClass]
    public class SqlServerStatisticsPublisherTests
    {
        public TestContext TestContext { get; set; }
        private static readonly TimeSpan timeout = TimeSpan.FromMinutes(1);
        private static string connectionString;
        private const string testDatabaseName = "OrleansTest";
        private const string adoInvariant = AdoNetInvariants.InvariantNameSqlServer;
        private const string dbName = "SqlServer";

        private static readonly TraceLogger logger = TraceLogger.GetLogger("MySqlStatisticsPublisherTests",
            TraceLogger.LoggerType.Application);

        private SqlStatisticsPublisher statisticsPublisher;

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            TraceLogger.Initialize(new NodeConfiguration());
            TraceLogger.AddTraceLevelOverride("MySqlStatisticsPublisherTests", Severity.Verbose3);

            connectionString =
                RelationalStorageForTesting.SetupInstance(adoInvariant, testDatabaseName)
                    .Result.CurrentConnectionString;
        }


        private async Task Initialize()
        {
            statisticsPublisher = new SqlStatisticsPublisher();
            await statisticsPublisher.Init("Test", new StatisticsPublisherProviderRuntime(logger),
                new StatisticsPublisherProviderConfig(adoInvariant, connectionString));
        }

        // Use TestCleanup to run code after each test has run
        [TestCleanup]
        public void TestCleanup()
        {
            logger.Info("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }


        [TestMethod, TestCategory("Statistics"), TestCategory(dbName)]
        public async Task SqlStatisticsPublisher_SqlServer_Init()
        {
            await Initialize();
            Assert.IsNotNull(statisticsPublisher, "Statistics publisher created");
        }


        [TestMethod, TestCategory("Statistics"), TestCategory(dbName)]
        public async Task SqlStatisticsPublisher_SqlServer_ReportMetrics_Client()
        {
            await Initialize();
            statisticsPublisher.AddConfiguration("statisticsDeployment", "statisticsHostName", "statisticsClient", IPAddress.Loopback);
            await RunParallel(10, () => statisticsPublisher.ReportMetrics((IClientPerformanceMetrics)new DummyPerformanceMetrics()));
        }

        [TestMethod, TestCategory("Statistics"), TestCategory(dbName)]
        public async Task SqlStatisticsPublisher_SqlServer_ReportStats()
        {
            await Initialize();
            statisticsPublisher.AddConfiguration("statisticsDeployment", "statisticsHostName", "statisticsClient", IPAddress.Loopback);
            await RunParallel(10, () => statisticsPublisher.ReportStats(new List<ICounter> { new DummyCounter() }));
        }

        [TestMethod, TestCategory("Statistics"), TestCategory(dbName)]
        public async Task SqlStatisticsPublisher_SqlServer_ReportMetrics_Silo()
        {
            GlobalConfiguration config = new GlobalConfiguration
            {
                DeploymentId = "statisticsDeployment",
                AdoInvariant = adoInvariant,
                DataConnectionString = connectionString
            };

            IMembershipTable mbr = new SqlMembershipTable();
            await mbr.InitializeMembershipTable(config, true, logger).WithTimeout(timeout);
            await Initialize();
            statisticsPublisher.AddConfiguration("statisticsDeployment", true, "statisticsSiloId", SiloAddress.NewLocalAddress(0), new IPEndPoint(IPAddress.Loopback, 12345), "statisticsHostName");
            await RunParallel(10, () => statisticsPublisher.ReportMetrics((ISiloPerformanceMetrics)new DummyPerformanceMetrics()));
        }

        private Task RunParallel(int count, Func<Task> taskFactory)
        {
            return Task.WhenAll(Enumerable.Range(0, count).Select(x => taskFactory()));
        }
    }
}
