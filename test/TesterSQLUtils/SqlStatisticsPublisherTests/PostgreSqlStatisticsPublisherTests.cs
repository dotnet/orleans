﻿using System.Threading.Tasks;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using Xunit;

namespace UnitTests.SqlStatisticsPublisherTests
{
    public class PostgreSqlStatisticsPublisherTests : SqlStatisticsPublisherTestsBase
    {
        public PostgreSqlStatisticsPublisherTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment)
        {
        }
        protected override string AdoInvariant
        {
            get { return AdoNetInvariants.InvariantNamePostgreSql; }
        }

        [Fact(Skip = "Not Implemented"), TestCategory("Statistics"), TestCategory("PostgreSql")]
        public void SqlStatisticsPublisher_PostgreSql_Init()
        {
        }

        [Fact(Skip = "Not Implemented"), TestCategory("Statistics"), TestCategory("PostgreSql")]
        public async Task SqlStatisticsPublisher_PostgreSql_ReportMetrics_Client()
        {
            await SqlStatisticsPublisher_ReportMetrics_Client();
        }

        [Fact(Skip = "Not Implemented"), TestCategory("Statistics"), TestCategory("PostgreSql")]
        public async Task SqlStatisticsPublisher_PostgreSql_ReportStats()
        {
            await SqlStatisticsPublisher_ReportStats();
        }

        [Fact(Skip = "Not Implemented"), TestCategory("Statistics"), TestCategory("PostgreSql")]
        public async Task SqlStatisticsPublisher_PostgreSql_ReportMetrics_Silo()
        {
            await SqlStatisticsPublisher_ReportMetrics_Silo();
        }
    }
}