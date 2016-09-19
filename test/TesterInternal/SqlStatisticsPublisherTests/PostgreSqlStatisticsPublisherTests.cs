using System.Threading.Tasks;
using Orleans.SqlUtils;
using Xunit;

namespace UnitTests.SqlStatisticsPublisherTests
{
    public class PostgreSqlStatisticsPublisherTests : SqlStatisticsPublisherTestsBase
    {
        public PostgreSqlStatisticsPublisherTests(ConnectionStringFixture fixture) : base(fixture)
        {
        }
        protected override string AdoInvariant
        {
            get { return AdoNetInvariants.InvariantNamePostgreSql; }
        }

        [Fact, TestCategory("Statistics"), TestCategory("PostgreSql")]
        public void SqlStatisticsPublisher_PostgreSql_Init()
        {
        }

        [Fact, TestCategory("Statistics"), TestCategory("PostgreSql")]
        public async Task SqlStatisticsPublisher_PostgreSql_ReportMetrics_Client()
        {
            await SqlStatisticsPublisher_ReportMetrics_Client();
        }

        [Fact, TestCategory("Statistics"), TestCategory("PostgreSql")]
        public async Task SqlStatisticsPublisher_PostgreSql_ReportStats()
        {
            await SqlStatisticsPublisher_ReportStats();
        }

        [Fact, TestCategory("Statistics"), TestCategory("PostgreSql")]
        public async Task SqlStatisticsPublisher_PostgreSql_ReportMetrics_Silo()
        {
            await SqlStatisticsPublisher_ReportMetrics_Silo();
        }
    }
}