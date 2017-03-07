using System.Threading.Tasks;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.SqlUtils;
using TestExtensions;
using UnitTests.General;
using Xunit;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using MySQL
    /// </summary>
    [TestCategory("Membership"), TestCategory("MySql")]
    public class MySqlMembershipTableTests : MembershipTableTestsBase
    {
        public MySqlMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment)
        {
            LogManager.AddTraceLevelOverride(typeof (MySqlMembershipTableTests).Name, Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(Logger logger)
        {
            return new SqlMembershipTable(this.GrainReferenceConverter);
        }

        protected override IGatewayListProvider CreateGatewayListProvider(Logger logger)
        {
            return new SqlMembershipTable(this.GrainReferenceConverter);
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNameMySql;
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
    }
}
