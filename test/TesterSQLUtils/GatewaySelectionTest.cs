using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.MessageCenterTests
{
    public class RelationalGatewaySelectionTest : GatewaySelectionTest
    {
        public RelationalGatewaySelectionTest(ITestOutputHelper output) : base(output)
        {

        }

        [Fact, TestCategory("Gateway"), TestCategory("SqlServer")]
        public async Task GatewaySelection_SqlServer()
        {
            string testName = Guid.NewGuid().ToString();// TestContext.TestName;

            Guid serviceId = Guid.NewGuid();

            GlobalConfiguration cfg = new GlobalConfiguration
            {
                ServiceId = serviceId,
                DeploymentId = testName,
                DataConnectionString = TestHelper.TestUtils.GetSqlConnectionString()
            };

            var membership = new SqlMembershipTable();
            var logger = LogManager.GetLogger(membership.GetType().Name);
            await membership.InitializeMembershipTable(cfg, true, logger);

            IMembershipTable membershipTable = membership;

            // Pre-populate gateway table with data
            int count = 1;
            foreach (Uri gateway in gatewayAddressUris)
            {
                output.WriteLine("Adding gataway data for {0}", gateway);

                SiloAddress siloAddress = gateway.ToSiloAddress();
                Assert.NotNull(siloAddress);

                MembershipEntry MembershipEntry = new MembershipEntry
                {
                    SiloAddress = siloAddress,
                    HostName = gateway.Host,
                    Status = SiloStatus.Active,
                    ProxyPort = gateway.Port,
                    StartTime = DateTime.UtcNow
                };

                var tableVersion = new TableVersion(count, Guid.NewGuid().ToString());

                output.WriteLine("Inserting gataway data for {0} with TableVersion={1}", MembershipEntry, tableVersion);

                bool ok = await membershipTable.InsertRow(MembershipEntry, tableVersion);
                count++;
                Assert.True(ok, $"Membership record should have been written OK but were not: {MembershipEntry}");

                output.WriteLine("Successfully inserted Membership row {0}", MembershipEntry);
            }

            MembershipTableData membershipTableData = await membershipTable.ReadAll();
            Assert.NotNull(membershipTableData);
            Assert.Equal(gatewayAddressUris.Count, membershipTableData.Members.Count);  // "Number of gateway records read"

            IGatewayListProvider listProvider = membership;

            Test_GatewaySelection(listProvider);
        }
    }
}
