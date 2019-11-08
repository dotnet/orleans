using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace UnitTests.MembershipTests
{
    internal static class SiloInstanceTableTestConstants
    {
        internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

        internal static readonly bool DeleteEntriesAfterTest = true; // false; // Set to false for Debug mode

        internal static readonly string INSTANCE_STATUS_CREATED = SiloStatus.Created.ToString();  //"Created";
        internal static readonly string INSTANCE_STATUS_ACTIVE = SiloStatus.Active.ToString();    //"Active";
        internal static readonly string INSTANCE_STATUS_DEAD = SiloStatus.Dead.ToString();        //"Dead";
    }

    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public abstract class MembershipTableTestsBase : IDisposable, IClassFixture<ConnectionStringFixture>
    {
        private readonly TestEnvironmentFixture environment;
        private static readonly string hostName = Dns.GetHostName();
        private readonly ILogger logger;
        private readonly IMembershipTable membershipTable;
        private readonly IGatewayListProvider gatewayListProvider;
        protected readonly string clusterId;
        protected readonly string connectionString;
        protected ILoggerFactory loggerFactory;
        protected IOptions<SiloOptions> siloOptions;
        protected IOptions<ClusterOptions> clusterOptions;
        protected const string testDatabaseName = "OrleansMembershipTest";//for relational storage
        protected readonly IOptions<GatewayOptions> gatewayOptions;
        protected readonly ClientConfiguration clientConfiguration;

        protected MembershipTableTestsBase(ConnectionStringFixture fixture, TestEnvironmentFixture environment, LoggerFilterOptions filters)
        {
            this.environment = environment;
            loggerFactory = TestingUtils.CreateDefaultLoggerFactory($"{this.GetType()}.log", filters);
            logger = loggerFactory.CreateLogger(this.GetType().FullName);

            this.clusterId = "test-" + Guid.NewGuid();

            logger.Info("ClusterId={0}", this.clusterId);

            fixture.InitializeConnectionStringAccessor(GetConnectionString);
            this.connectionString = fixture.ConnectionString;
            this.clusterOptions = Options.Create(new ClusterOptions { ClusterId = this.clusterId });
            var adoVariant = GetAdoInvariant();

            membershipTable = CreateMembershipTable(logger);
            membershipTable.InitializeMembershipTable(true).WithTimeout(TimeSpan.FromMinutes(1)).Wait();

            clientConfiguration = new ClientConfiguration
            {
                ClusterId = this.clusterId,
                AdoInvariant = adoVariant,
                DataConnectionString = fixture.ConnectionString
            };

            this.gatewayOptions = Options.Create(new GatewayOptions());
            gatewayListProvider = CreateGatewayListProvider(logger);
            gatewayListProvider.InitializeGatewayListProvider().WithTimeout(TimeSpan.FromMinutes(1)).Wait();
        }

        public IGrainFactory GrainFactory => this.environment.GrainFactory;

        public IGrainReferenceConverter GrainReferenceConverter => this.environment.Services.GetRequiredService<IGrainReferenceConverter>();

        public IServiceProvider Services => this.environment.Services;

        public void Dispose()
        {
            if (membershipTable != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                membershipTable.DeleteMembershipTableEntries(this.clusterId).Wait();
            }
            this.loggerFactory.Dispose();
        }

        protected abstract IGatewayListProvider CreateGatewayListProvider(ILogger logger);
        protected abstract IMembershipTable CreateMembershipTable(ILogger logger);
        protected abstract Task<string> GetConnectionString();

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

            // only members with a non-zero Gateway port
            Assert.DoesNotContain(membershipEntries[3].SiloAddress.ToGatewayUri().ToString(), entries);

            // only Active members
            Assert.Contains(membershipEntries[5].SiloAddress.ToGatewayUri().ToString(), entries);
            Assert.Contains(membershipEntries[9].SiloAddress.ToGatewayUri().ToString(), entries);
            Assert.Equal(2, entries.Count);
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

            var insertions = Task.WhenAll(Enumerable.Range(1, 20).Select(async i => { try { return await membershipTable.InsertRow(data, newTableVer); } catch { return false; } }));

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
                    try { done = await membershipTable.UpdateRow(updatedRow.Item1, updatedRow.Item2, tableVersion); } catch { done = false; }
                } while (!done);
            })).WithTimeout(TimeSpan.FromSeconds(30));


            tableData = await membershipTable.ReadAll();
            Assert.NotNull(tableData.Version);

            if (extendedProtocol)
                Assert.Equal(20, tableData.Version.Version);

            Assert.Equal(1, tableData.Members.Count);
        }

        protected async Task MembershipTable_UpdateIAmAlive(bool extendedProtocol = true)
        {
            MembershipTableData tableData = await membershipTable.ReadAll();

            TableVersion newTableVersion = tableData.Version.Next();
            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membershipTable.InsertRow(newEntry, newTableVersion);
            Assert.True(ok);


            var amAliveTime = DateTime.UtcNow;

            // This mimics the arguments MembershipOracle.OnIAmAliveUpdateInTableTimer passes in
            var entry = new MembershipEntry
            {
                SiloAddress = newEntry.SiloAddress,
                IAmAliveTime = amAliveTime
            };

            await membershipTable.UpdateIAmAlive(entry);

            tableData = await membershipTable.ReadAll();
            Tuple<MembershipEntry, string> member = tableData.Members.First();
            // compare that the value is close to what we passed in, but not exactly, as the underlying store can set its own precision settings
            // (ie: in SQL Server this is defined as datetime2(3), so we don't expect precision to account for less than 0.001s values)
            Assert.True((amAliveTime - member.Item1.IAmAliveTime).Duration() < TimeSpan.FromMilliseconds(50), (amAliveTime - member.Item1.IAmAliveTime).Duration().ToString());
        }

        protected async Task MembershipTable_CleanupDefunctSiloEntries(bool extendedProtocol = true)
        {
            MembershipTableData data = await membershipTable.ReadAll();
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.Equal(0, data.Members.Count);

            TableVersion newTableVersion = data.Version.Next();

            MembershipEntry oldEntry = CreateMembershipEntryForTest();
            oldEntry.IAmAliveTime = oldEntry.IAmAliveTime.AddDays(-10);
            oldEntry.StartTime = oldEntry.StartTime.AddDays(-10);
            bool ok = await membershipTable.InsertRow(oldEntry, newTableVersion);

            Assert.True(ok, "InsertRow failed");

            newTableVersion = newTableVersion.Next();
            MembershipEntry newEntry = CreateMembershipEntryForTest();
            ok = await membershipTable.InsertRow(newEntry, newTableVersion);

            Assert.True(ok, "InsertRow failed");

            data = await membershipTable.ReadAll();
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.Equal(2, data.Members.Count);


            await membershipTable.CleanupDefunctSiloEntries(oldEntry.IAmAliveTime.AddDays(3));

            data = await membershipTable.ReadAll();
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.Equal(1, data.Members.Count);
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
            return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc);
        }

        private static SiloAddress CreateSiloAddressForTest()
        {
            var siloAddress = SiloAddressUtils.NewLocalSiloAddress(Interlocked.Increment(ref generation));
            siloAddress.Endpoint.Port = 12345;
            return siloAddress;
        }
    }
}
