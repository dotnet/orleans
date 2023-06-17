using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.Runtime.MembershipService;
using Orleans.Tests.SqlUtils;
using Orleans.Configuration;
using TestExtensions;
using UnitTests.General;
using Xunit;

namespace UnitTests.MembershipTests
{
    [TestCategory("Membership"), TestCategory("PostgreSql"), TestCategory("Functional")]
    public class PostgreSqlMembershipTableTests : MembershipTableTestsBase
    {
        public PostgreSqlMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
        {
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter(typeof(PostgreSqlMembershipTableTests).Name, LogLevel.Trace);
            return filters;
        }

        protected override IMembershipTable CreateMembershipTable(ILogger logger)
        {
            var options = new AdoNetClusteringSiloOptions()
            {
                Invariant = GetAdoInvariant(),
                ConnectionString = this.connectionString,
            };
            return new AdoNetClusteringTable(this.Services, this._clusterOptions, Options.Create(options), this.loggerFactory.CreateLogger<AdoNetClusteringTable>());
        }

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            var options = new AdoNetClusteringClientOptions()
            {
                ConnectionString = this.connectionString,
                Invariant = GetAdoInvariant()
            };
            return new AdoNetGatewayListProvider(this.loggerFactory.CreateLogger<AdoNetGatewayListProvider>(), this.Services, Options.Create(options), this._gatewayOptions, this._clusterOptions);
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNamePostgreSql;
        }

        protected override async Task<string> GetConnectionString()
        {
            var instance = await RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName);
            return instance.CurrentConnectionString;
        }

        [SkippableFact]
        public void MembershipTable_PostgreSql_Init()
        {
        }

        [SkippableFact]
        public async Task MembershipTable_PostgreSql_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [SkippableFact]
        public async Task MembershipTable_PostgreSql_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [SkippableFact]
        public async Task MembershipTable_PostgreSql_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [SkippableFact]
        public async Task MembershipTable_PostgreSql_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [SkippableFact]
        public async Task MembershipTable_PostgreSql_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [SkippableFact]
        public async Task MembershipTable_PostgreSql_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [SkippableFact]
        public async Task MembershipTable_PostgreSql_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }

        [SkippableFact]
        public async Task MembershipTable_PostgreSql_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }

        [SkippableFact]
        public async Task MembershipTablePostgreSql_CleanupDefunctSiloEntries()
        {
            await MembershipTable_CleanupDefunctSiloEntries();
        }
    }
}
