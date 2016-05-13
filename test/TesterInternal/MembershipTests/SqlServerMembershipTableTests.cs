using System.Threading.Tasks;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.SqlUtils;
using UnitTests.General;
using Xunit;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using SQL Server
    /// </summary>
    public class SqlServerMembershipTableTests : MembershipTableTestsBase
    {
        public SqlServerMembershipTableTests(ConnectionStringFixture fixture) : base(fixture)
        {
            TraceLogger.AddTraceLevelOverride(typeof (SqlServerMembershipTableTests).Name, Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(TraceLogger logger)
        {
            return new SqlMembershipTable();
        }

        protected override IGatewayListProvider CreateGatewayListProvider(TraceLogger logger)
        {
            return new SqlMembershipTable();
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNameSqlServer;
        }

        protected override string GetConnectionString()
        {
            return
                RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName)
                    .Result.CurrentConnectionString;
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public void MembershipTable_SqlServer_Init()
        {
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }
    }
}
