using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Membership;
using Orleans.Runtime.MembershipService;
using Orleans.Tests.SqlUtils;
using OrleansSQLUtils.Configuration;
using TestExtensions;
using UnitTests.General;
using Xunit;
using OrleansSQLUtils;
using OrleansSQLUtils.Options;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using SQL Server
    /// </summary>
    [TestCategory("Membership"), TestCategory("SqlServer")]
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
            var options = new SqlMembershipOptions()
            {
                AdoInvariant = GetAdoInvariant(),
                ConnectionString = this.connectionString,
            };
            return new SqlMembershipTable(this.GrainReferenceConverter, this.siloOptions, Options.Create(options),  this.loggerFactory.CreateLogger<SqlMembershipTable>());
        }

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            var options = new SqlGatewayListProviderOptions()
            {
                ConnectionString = this.connectionString,
                AdoInvariant = GetAdoInvariant()
            };
            return new SqlGatewayListProvider(this.loggerFactory.CreateLogger<SqlGatewayListProvider>(), this.GrainReferenceConverter, this.clientConfiguration, Options.Create(options), this.clientOptions);
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
