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
    /// Tests for operation of Orleans Membership Table using SQL Server
    /// </summary>
    [TestCategory("Membership"), TestCategory("AdoNet")]
    public class SqlServerMembershipTableTests : MembershipTableTestsBase
    {
        public SqlServerMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
        {
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter(typeof(SqlServerMembershipTableTests).Name, LogLevel.Trace);
            return filters;
        }
        protected override IMembershipTable CreateMembershipTable(ILogger logger)
        {
            var options = new AdoNetClusteringSiloOptions()
            {
                Invariant = GetAdoInvariant(),
                ConnectionString = this.connectionString,
            };
            return new AdoNetClusteringTable(this.GrainReferenceConverter, this.clusterOptions, Options.Create(options),  this.loggerFactory.CreateLogger<AdoNetClusteringTable>());
        }

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            var options = new AdoNetClusteringClientOptions()
            {
                ConnectionString = this.connectionString,
                Invariant = GetAdoInvariant()
            };
            return new AdoNetGatewayListProvider(this.loggerFactory.CreateLogger<AdoNetGatewayListProvider>(), this.GrainReferenceConverter, Options.Create(options), gatewayOptions, this.clusterOptions);
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNameSqlServer;
        }

        protected override async Task<string> GetConnectionString()
        {
            var instance = await RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName);
            return instance.CurrentConnectionString;
        }

        [SkippableFact]
        public void MembershipTable_SqlServer_Init()
        {
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_SqlServer_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_SqlServer_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_SqlServer_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_SqlServer_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_SqlServer_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_SqlServer_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_SqlServer_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_SqlServer_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }
    }
}
