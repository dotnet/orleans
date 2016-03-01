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
    /// Tests for operation of Orleans Membership Table using SQL Server
    /// </summary>
    [TestClass]
    public class SqlServerMembershipTableTests : MembershipTableTestsBase
    {
        private const string testDatabaseName = "OrleansTest";

        [ClassInitialize]
        public new static void ClassInitialize(TestContext testContext)
        {
            MembershipTableTestsBase.ClassInitialize();
            TraceLogger.AddTraceLevelOverride(typeof (SqlServerMembershipTableTests).Name, Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(TraceLogger logger)
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

        [TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public void MembershipTable_SqlServer_Init()
        {
        }

        [TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        [TestMethod, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task MembershipTable_SqlServer_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }
    }
}
