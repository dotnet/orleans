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
    public class PostgreSqlMembershipTableTests : MembershipTableTestsBase
    {
        public PostgreSqlMembershipTableTests(ConnectionStringFixture fixture) : base(fixture)
        {
            LogManager.AddTraceLevelOverride(typeof(PostgreSqlMembershipTableTests).Name, Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(Logger logger)
        {
            return new SqlMembershipTable();
        }

        protected override IGatewayListProvider CreateGatewayListProvider(Logger logger)
        {
            return new SqlMembershipTable();
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNamePostgreSql;
        }

        protected override string GetConnectionString()
        {
            return
                RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName)
                    .Result.CurrentConnectionString;
        }


        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public void MembershipTable_PostgreSql_Init()
        {
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task MembershipTable_PostgreSql_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task MembershipTable_PostgreSql_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task MembershipTable_PostgreSql_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task MembershipTable_PostgreSql_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task MembershipTable_PostgreSql_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task MembershipTable_PostgreSql_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task MembershipTable_PostgreSql_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }
    }
}
