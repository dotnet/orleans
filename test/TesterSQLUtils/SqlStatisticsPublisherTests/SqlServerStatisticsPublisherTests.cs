using System.Threading.Tasks;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using Xunit;

namespace UnitTests.SqlStatisticsPublisherTests
{
    /// <summary>
    /// Tests for operation of Orleans Statistics Publisher using SQL Server
    /// </summary>    
    public class SqlServerStatisticsPublisherTests : SqlStatisticsPublisherTestsBase
    {
        public SqlServerStatisticsPublisherTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment)
        {
        }
        protected override string AdoInvariant
        {
            get { return AdoNetInvariants.InvariantNameSqlServer; }
        }
        
        [Fact(Skip = "Not Implemented"), TestCategory("Statistics"), TestCategory("SqlServer")]
        public void SqlStatisticsPublisher_SqlServer_Init()
        {
        }

        [Fact(Skip = "Not Implemented"), TestCategory("Statistics"), TestCategory("SqlServer")]
        public async Task SqlStatisticsPublisher_SqlServer_ReportMetrics_Client()
        {
            await SqlStatisticsPublisher_ReportMetrics_Client();
        }

        [Fact(Skip = "Not Implemented"), TestCategory("Statistics"), TestCategory("SqlServer")]
        public async Task SqlStatisticsPublisher_SqlServer_ReportStats()
        {
            await SqlStatisticsPublisher_ReportStats();
        }

        [Fact(Skip = "Not Implemented"), TestCategory("Statistics"), TestCategory("SqlServer")]
        public async Task SqlStatisticsPublisher_SqlServer_ReportMetrics_Silo()
        {
            await SqlStatisticsPublisher_ReportMetrics_Silo();
        }
    }
}
