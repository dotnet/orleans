using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using UnitTests.StorageTests;
using Xunit;

namespace UnitTests.MembershipTests
{
    public abstract class MembershipTableTestsBase : IDisposable, IClassFixture<ConnectionStringFixture>
    {
        private static readonly string hostName = Dns.GetHostName();
        private readonly Logger logger;
        private readonly IMembershipTable membershipTable;
        private readonly IGatewayListProvider gatewayListProvider;
        private readonly string deploymentId;

        protected const string testDatabaseName = "OrleansMembershipTest";//for relational storage

        protected MembershipTableTestsBase(ConnectionStringFixture fixture)
        {
            LogManager.Initialize(new NodeConfiguration());
            logger = LogManager.GetLogger(GetType().Name, LoggerType.Application);
            deploymentId = "test-" + Guid.NewGuid();

            logger.Info("DeploymentId={0}", deploymentId);

            lock (fixture.SyncRoot)
            {
                if (fixture.ConnectionString == null)
                    fixture.ConnectionString = GetConnectionString();
            }
            var globalConfiguration = new GlobalConfiguration
            {
                DeploymentId = deploymentId,
                AdoInvariant = GetAdoInvariant(),
                DataConnectionString = fixture.ConnectionString
            };

            membershipTable = CreateMembershipTable(logger);
            membershipTable.InitializeMembershipTable(globalConfiguration, true, logger).WithTimeout(TimeSpan.FromMinutes(1)).Wait();

            var clientConfiguration = new ClientConfiguration
            {
                DeploymentId = globalConfiguration.DeploymentId,
                AdoInvariant = globalConfiguration.AdoInvariant,
                DataConnectionString = globalConfiguration.DataConnectionString
            };

            gatewayListProvider = CreateGatewayListProvider(logger);
            gatewayListProvider.InitializeGatewayListProvider(clientConfiguration, logger).WithTimeout(TimeSpan.FromMinutes(1)).Wait();
        }

        public void Dispose()
        {
            if (membershipTable != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                membershipTable.DeleteMembershipTableEntries(deploymentId).Wait();
            }
        }

        protected abstract IGatewayListProvider CreateGatewayListProvider(Logger logger);
        protected abstract IMembershipTable CreateMembershipTable(Logger logger);
        protected abstract string GetConnectionString();

        protected virtual string GetAdoInvariant()
        {
            return null;
        }

        protected async Task MembershipTable_GetGateways()
        {
            var membershipEntries = Enumerable.Range(0, 10).Select(i => CreateMembershipEntryForTest()).ToArray();

            membershipEntries[3].Status = SiloStatus.Active;
            membershipEntries[3].ProxyPort = 0;
            membershipEntries[5].Status = SiloStatus.Active;
            membershipEntries[9].Status = SiloStatus.Active;

            var data = await membershipTable.ReadAll();
            Assert.NotNull(data);
            Assert.Equal(0, data.Members.Count);

            var version = data.Version;
            foreach (var membershipEntry in membershipEntries)
            {
                Assert.True(await membershipTable.InsertRow(membershipEntry, version));
                version = (await membershipTable.ReadRow(membershipEntry.SiloAddress)).Version;
            }

            var gateways = await gatewayListProvider.GetGateways();

            var entries = new List<string>(gateways.Select(g => g.ToString()));

            Assert.True(entries.Contains(membershipEntries[5].SiloAddress.ToGatewayUri().ToString()));
            Assert.True(entries.Contains(membershipEntries[9].SiloAddress.ToGatewayUri().ToString()));
        }

        protected async Task MembershipTable_ReadAll_EmptyTable()
        {
            var data = await membershipTable.ReadAll();
            Assert.NotNull(data);

            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.Equal(0, data.Members.Count);
            Assert.NotNull(data.Version.VersionEtag);
            Assert.Equal(0, data.Version.Version);
        }

        protected async Task MembershipTable_InsertRow(bool extendedProtocol = true)
        {
            var membershipEntry = CreateMembershipEntryForTest();

            var data = await membershipTable.ReadAll();
            Assert.NotNull(data);
            Assert.Equal(0, data.Members.Count);

            TableVersion nextTableVersion = data.Version.Next();

            bool ok = await membershipTable.InsertRow(membershipEntry, nextTableVersion);
            Assert.True(ok, "InsertRow failed");

            data = await membershipTable.ReadAll();

            if (extendedProtocol)
                Assert.Equal(1, data.Version.Version);

            Assert.Equal(1, data.Members.Count);
        }

        protected async Task MembershipTable_ReadRow_Insert_Read(bool extendedProtocol = true)
        {
            MembershipTableData data = await membershipTable.ReadAll();

            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.Equal(0, data.Members.Count);

            TableVersion newTableVersion = data.Version.Next();

            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membershipTable.InsertRow(newEntry, newTableVersion);

            Assert.True(ok, "InsertRow failed");

            ok = await membershipTable.InsertRow(newEntry, newTableVersion);
            Assert.False(ok, "InsertRow should have failed - same entry, old table version");

            if (extendedProtocol)
            {
                ok = await membershipTable.InsertRow(CreateMembershipEntryForTest(), newTableVersion);
                Assert.False(ok, "InsertRow should have failed - new entry, old table version");
            }

            data = await membershipTable.ReadAll();

            if (extendedProtocol)
                Assert.Equal(1, data.Version.Version);

            TableVersion nextTableVersion = data.Version.Next();

            ok = await membershipTable.InsertRow(newEntry, nextTableVersion);
            Assert.False(ok, "InsertRow should have failed - duplicate entry");

            data = await membershipTable.ReadAll();

            Assert.Equal(1, data.Members.Count);

            data = await membershipTable.ReadRow(newEntry.SiloAddress);
            if (extendedProtocol)
                Assert.Equal(newTableVersion.Version, data.Version.Version);

            logger.Info("Membership.ReadRow returned VableVersion={0} Data={1}", data.Version, data);

            Assert.Equal(1, data.Members.Count);
            Assert.NotNull(data.Version.VersionEtag);
            if (extendedProtocol)
            {
                Assert.NotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag);
                Assert.Equal(newTableVersion.Version, data.Version.Version);
            }
            var membershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.Info("Membership.ReadRow returned MembershipEntry ETag={0} Entry={1}", eTag, membershipEntry);

            Assert.NotNull(eTag);
            Assert.NotNull(membershipEntry);
        }

        protected async Task MembershipTable_ReadAll_Insert_ReadAll(bool extendedProtocol = true)
        {
            MembershipTableData data = await membershipTable.ReadAll();
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.Equal(0, data.Members.Count);

            TableVersion newTableVersion = data.Version.Next();

            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membershipTable.InsertRow(newEntry, newTableVersion);

            Assert.True(ok, "InsertRow failed");

            data = await membershipTable.ReadAll();
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.Equal(1, data.Members.Count);
            Assert.NotNull(data.Version.VersionEtag);

            if (extendedProtocol)
            {
                Assert.NotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag);
                Assert.Equal(newTableVersion.Version, data.Version.Version);
            }

            var membershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.Info("Membership.ReadAll returned MembershipEntry ETag={0} Entry={1}", eTag, membershipEntry);

            Assert.NotNull(eTag);
            Assert.NotNull(membershipEntry);
        }

        protected async Task MembershipTable_UpdateRow(bool extendedProtocol = true)
        {
            var tableData = await membershipTable.ReadAll();
            Assert.NotNull(tableData.Version);

            Assert.Equal(0, tableData.Version.Version);
            Assert.Equal(0, tableData.Members.Count);

            for (int i = 1; i < 10; i++)
            {
                var siloEntry = CreateMembershipEntryForTest();

                siloEntry.SuspectTimes =
                    new List<Tuple<SiloAddress, DateTime>>
                    {
                        new Tuple<SiloAddress, DateTime>(CreateSiloAddressForTest(), GetUtcNowWithSecondsResolution().AddSeconds(1)),
                        new Tuple<SiloAddress, DateTime>(CreateSiloAddressForTest(), GetUtcNowWithSecondsResolution().AddSeconds(2))
                    };

                TableVersion tableVersion = tableData.Version.Next();

                logger.Info("Calling InsertRow with Entry = {0} TableVersion = {1}", siloEntry, tableVersion);
                bool ok = await membershipTable.InsertRow(siloEntry, tableVersion);

                Assert.True(ok, "InsertRow failed");

                tableData = await membershipTable.ReadAll();

                var etagBefore = tableData.Get(siloEntry.SiloAddress).Item2;

                Assert.NotNull(etagBefore);

                if (extendedProtocol)
                {
                    logger.Info("Calling UpdateRow with Entry = {0} correct eTag = {1} old version={2}", siloEntry,
                                etagBefore, tableVersion != null ? tableVersion.ToString() : "null");
                    ok = await membershipTable.UpdateRow(siloEntry, etagBefore, tableVersion);
                    Assert.False(ok, $"row update should have failed - Table Data = {tableData}");
                    tableData = await membershipTable.ReadAll();
                }

                tableVersion = tableData.Version.Next();

                logger.Info("Calling UpdateRow with Entry = {0} correct eTag = {1} correct version={2}", siloEntry,
                    etagBefore, tableVersion != null ? tableVersion.ToString() : "null");

                ok = await membershipTable.UpdateRow(siloEntry, etagBefore, tableVersion);

                Assert.True(ok, $"UpdateRow failed - Table Data = {tableData}");

                logger.Info("Calling UpdateRow with Entry = {0} old eTag = {1} old version={2}", siloEntry,
                    etagBefore, tableVersion != null ? tableVersion.ToString() : "null");
                ok = await membershipTable.UpdateRow(siloEntry, etagBefore, tableVersion);
                Assert.False(ok, $"row update should have failed - Table Data = {tableData}");

                tableData = await membershipTable.ReadAll();

                var tuple = tableData.Get(siloEntry.SiloAddress);

                Assert.Equal(tuple.Item1.ToFullString(true), siloEntry.ToFullString(true));

                var etagAfter = tuple.Item2;

                if (extendedProtocol)
                {
                    logger.Info("Calling UpdateRow with Entry = {0} correct eTag = {1} old version={2}", siloEntry,
                                etagAfter, tableVersion != null ? tableVersion.ToString() : "null");

                    ok = await membershipTable.UpdateRow(siloEntry, etagAfter, tableVersion);

                    Assert.False(ok, $"row update should have failed - Table Data = {tableData}");
                }

                tableData = await membershipTable.ReadAll();

                etagBefore = etagAfter;

                etagAfter = tableData.Get(siloEntry.SiloAddress).Item2;

                Assert.Equal(etagBefore, etagAfter);
                Assert.NotNull(tableData.Version);
                if (extendedProtocol)
                    Assert.Equal(tableVersion.Version, tableData.Version.Version);

                Assert.Equal(i, tableData.Members.Count);
            }
        }

        protected async Task MembershipTable_UpdateRowInParallel(bool extendedProtocol = true)
        {
            var tableData = await membershipTable.ReadAll();

            var data = CreateMembershipEntryForTest();

            TableVersion newTableVer = tableData.Version.Next();

            var insertions = Task.WhenAll(Enumerable.Range(1, 20).Select(i => membershipTable.InsertRow(data, newTableVer)));

            Assert.True((await insertions).Single(x => x), "InsertRow failed");

            await Task.WhenAll(Enumerable.Range(1, 19).Select(async i =>
            {
                bool done;
                do
                {
                    var updatedTableData = await membershipTable.ReadAll();
                    var updatedRow = updatedTableData.Get(data.SiloAddress);

                    TableVersion tableVersion = updatedTableData.Version.Next();

                    await Task.Delay(10);
                    done = await membershipTable.UpdateRow(updatedRow.Item1, updatedRow.Item2, tableVersion);
                } while (!done);
            })).WithTimeout(TimeSpan.FromSeconds(30));


            tableData = await membershipTable.ReadAll();
            Assert.NotNull(tableData.Version);

            if (extendedProtocol)
                Assert.Equal(20, tableData.Version.Version);

            Assert.Equal(1, tableData.Members.Count);
        }

        private static int generation;
        // Utility methods
        private static MembershipEntry CreateMembershipEntryForTest()
        {
            SiloAddress siloAddress = CreateSiloAddressForTest();


            var membershipEntry = new MembershipEntry
            {
                SiloAddress = siloAddress,
                HostName = hostName,
                SiloName = "TestSiloName",
                Status = SiloStatus.Joining,
                ProxyPort = siloAddress.Endpoint.Port,
                StartTime = GetUtcNowWithSecondsResolution(),
                IAmAliveTime = GetUtcNowWithSecondsResolution()
            };

            return membershipEntry;
        }

        private static DateTime GetUtcNowWithSecondsResolution()
        {
            var now = DateTime.UtcNow;
            return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
        }

        private static SiloAddress CreateSiloAddressForTest()
        {
            var siloAddress = SiloAddress.NewLocalAddress(Interlocked.Increment(ref generation));
            siloAddress.Endpoint.Port = 12345;
            return siloAddress;
        }
    }
}
