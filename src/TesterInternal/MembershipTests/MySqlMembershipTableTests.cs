using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.SqlUtils;
using UnitTests.General;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using MySQL
    /// </summary>
    [TestClass]
    public class MySqlMembershipTableTests : MembershipTableTestsBase
    {
        private const string testDatabaseName = "OrleansTest";

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public new static void ClassInitialize(TestContext testContext)
        {
            MembershipTableTestsBase.ClassInitialize();
            TraceLogger.AddTraceLevelOverride(typeof (MySqlMembershipTableTests).Name, Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(TraceLogger logger)
        {
            return new SqlMembershipTable();
        }

        protected override string GetAdoInvariant()
        {
            return AdoNetInvariants.InvariantNameMySql;
        }

        protected override string GetConnectionString()
        {
            return
                RelationalStorageForTesting.SetupInstance(GetAdoInvariant(), testDatabaseName)
                    .Result.CurrentConnectionString;
        }


        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public void MembershipTable_MySql_Init()
        {
        }

        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("MySql")]
        public async Task MembershipTable_MySql_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }
    }
}
