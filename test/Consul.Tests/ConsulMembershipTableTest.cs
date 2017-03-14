#if !NETSTANDARD_TODO
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Host;
using System.Threading.Tasks;
using UnitTests;
using UnitTests.MembershipTests;
using Xunit;

namespace Consul.Tests
{
    /// <summary>
    /// Tests for operation of Orleans Membership Table using Consul - Requires access to external Consul cluster
    /// </summary>
    [TestCategory("Membership"), TestCategory("Consul")]
    public class ConsulMembershipTableTest : MembershipTableTestsBase
    {
        public ConsulMembershipTableTest(ConnectionStringFixture fixture) : base(fixture)
        {
            LogManager.AddTraceLevelOverride("ConsulBasedMembershipTable", Severity.Verbose3);
            LogManager.AddTraceLevelOverride("Storage", Severity.Verbose3);
        }

        protected override IMembershipTable CreateMembershipTable(Logger logger)
        {
            ConsulTestUtils.EnsureConsul();

            return new ConsulBasedMembershipTable();
        }

        protected override IGatewayListProvider CreateGatewayListProvider(Logger logger)
        {
            ConsulTestUtils.EnsureConsul();

            return new ConsulBasedMembershipTable();
        }

        protected override string GetConnectionString()
        {
            return ConsulTestUtils.CONSUL_ENDPOINT;
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_InsertRow()
        {
            await MembershipTable_InsertRow(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_UpdateRow()
        {
            await MembershipTable_UpdateRow(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task MembershipTable_DynamoDB_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel(false);
        }
    }
}

#endif