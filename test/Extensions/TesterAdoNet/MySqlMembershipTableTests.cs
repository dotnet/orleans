using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.Runtime.MembershipService;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using Xunit;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using MySQL
    /// </summary>
    [TestCategory("Membership"), TestCategory("MySql"), TestCategory("Functional")]
    public class MySqlMembershipTableTests : MembershipTableTestsBase
    {
        public MySqlMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
        {
        }

        protected override string GetAdoInvariant() => AdoNetInvariants.InvariantNameMySql;

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter(typeof(MySqlMembershipTableTests).Name, LogLevel.Trace);
            return filters;
        }

        protected override IMembershipTable CreateMembershipTable(ILogger logger)
        {
            var options = new AdoNetClusteringSiloOptions()
            {
                Invariant = GetAdoInvariant(),
                ConnectionString = this.connectionString,
            };
            return new AdoNetClusteringTable(this.Services, this.clusterOptions, Options.Create(options), loggerFactory.CreateLogger<AdoNetClusteringTable>());
        }

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            var options = new AdoNetClusteringClientOptions()
            {
                ConnectionString = this.connectionString,
                Invariant = GetAdoInvariant()
            };
            return new AdoNetGatewayListProvider(loggerFactory.CreateLogger<AdoNetGatewayListProvider>(), this.Services, Options.Create(options), this.gatewayOptions, this.clusterOptions);
        }

        protected override async Task<string> GetConnectionString()
        {
            var instance = await RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName);
            return instance.CurrentConnectionString;
        }

        [SkippableFact]
        public void MembershipTable_MySql_Init()
        {
        }

        [SkippableFact]
        public async Task MembershipTable_MySql_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [SkippableFact]
        public async Task MembershipTable_MySql_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [SkippableFact]
        public async Task MembershipTable_MySql_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [SkippableFact]
        public async Task MembershipTable_MySql_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [SkippableFact]
        public async Task MembershipTable_MySql_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [SkippableFact]
        public async Task MembershipTable_MySql_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [SkippableFact]
        public async Task MembershipTable_MySql_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }

        [SkippableFact]
        public async Task MembershipTable_MySql_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }

        [SkippableFact]
        public async Task MembershipTable_MySql_CleanupDefunctSiloEntries()
        {
            await MembershipTable_CleanupDefunctSiloEntries();
        }
    }
}
