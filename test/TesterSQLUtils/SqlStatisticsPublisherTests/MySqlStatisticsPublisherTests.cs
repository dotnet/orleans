using System.Threading.Tasks;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using Xunit;

namespace UnitTests.SqlStatisticsPublisherTests
{
    /// <summary>
    /// Tests for operation of Orleans Statistics Publisher using MySql
    /// </summary>
    public class MySqlStatisticsPublisherTests : SqlStatisticsPublisherTestsBase
    {
        public MySqlStatisticsPublisherTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment)
        {
        }
        protected override string AdoInvariant
        {
            get { return AdoNetInvariants.InvariantNameMySql; }
        }

        [Fact, TestCategory("Statistics"), TestCategory("MySql")]
        public void SqlStatisticsPublisher_MySql_Init()
        {
        }

        [Fact, TestCategory("Statistics"), TestCategory("MySql")]
        public async Task SqlStatisticsPublisher_MySql_ReportMetrics_Client()
        {
            await SqlStatisticsPublisher_ReportMetrics_Client();
        }

        [Fact, TestCategory("Statistics"), TestCategory("MySql")]
        public async Task SqlStatisticsPublisher_MySql_ReportStats()
        {
            await SqlStatisticsPublisher_ReportStats();
        }

        [Fact, TestCategory("Statistics"), TestCategory("MySql")]
        public async Task SqlStatisticsPublisher_MySql_ReportMetrics_Silo()
        {
            await SqlStatisticsPublisher_ReportMetrics_Silo();
        }
    }
}
