using System.Net;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

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
        private static readonly string hostName = Dns.GetHostName();
        private readonly ILogger logger;
        private readonly IMembershipTable membershipTable;
        private readonly IGatewayListProvider gatewayListProvider;
        protected readonly TestEnvironmentFixture environment;
        protected readonly string clusterId;
        protected readonly string connectionString;
        protected ILoggerFactory loggerFactory;
        protected IOptions<SiloOptions>? siloOptions;
        protected IOptions<ClusterOptions> _clusterOptions;
        protected const string testDatabaseName = "OrleansMembershipTest";//for relational storage
        protected readonly IOptions<GatewayOptions> _gatewayOptions;

        private static int generation;

        protected MembershipTableTestsBase(ConnectionStringFixture fixture, TestEnvironmentFixture environment, LoggerFilterOptions filters)
        {
            this.environment = environment;
            loggerFactory = TestingUtils.CreateDefaultLoggerFactory($"{this.GetType()}.log", filters);
            logger = loggerFactory.CreateLogger(this.GetType().FullName!);

            this.clusterId = "test-" + Guid.NewGuid();

            logger.LogInformation("ClusterId={ClusterId}", this.clusterId);

            fixture.InitializeConnectionStringAccessor(GetConnectionString);
            this.connectionString = fixture.ConnectionString;
            if (string.IsNullOrEmpty(this.connectionString))
            {
                throw Xunit.Sdk.SkipException.ForSkip("No connection string configured");
            }
            this._clusterOptions = Options.Create(new ClusterOptions { ClusterId = this.clusterId });
            var adoVariant = GetAdoInvariant();

            membershipTable = CreateMembershipTable(logger);
            using var initialization = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            initialization.CancelAfter(SiloInstanceTableTestConstants.Timeout);
            membershipTable.InitializeMembershipTableAsync(true, initialization.Token).Wait();

            this._gatewayOptions = Options.Create(new GatewayOptions());
            gatewayListProvider = CreateGatewayListProvider(logger);
            gatewayListProvider.InitializeGatewayListProvider().WaitAsync(TimeSpan.FromMinutes(1)).Wait();
        }

        public IGrainFactory GrainFactory => this.environment.GrainFactory;

        public IServiceProvider Services => this.environment.Services;

        public void Dispose()
        {
            try
            {
                if (membershipTable != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
                {
                    using var cleanup = new CancellationTokenSource(SiloInstanceTableTestConstants.Timeout);
                    membershipTable.DeleteMembershipTableEntriesAsync(this.clusterId, cleanup.Token).Wait();
                }
            }
            finally
            {
                this.loggerFactory.Dispose();
            }
        }

        protected abstract IGatewayListProvider CreateGatewayListProvider(ILogger logger);
        protected abstract IMembershipTable CreateMembershipTable(ILogger logger);
        protected abstract Task<string> GetConnectionString();

        protected virtual string? GetAdoInvariant()
        {
            return null;
        }

        protected async Task MembershipTable_GetGateways()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipEntries = Enumerable.Range(0, 10).Select(i => CreateMembershipEntryForTest()).ToArray();

            membershipEntries[3].Status = SiloStatus.Active;
            membershipEntries[3].ProxyPort = 0;
            membershipEntries[5].Status = SiloStatus.Active;
            membershipEntries[9].Status = SiloStatus.Active;

            var data = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.NotNull(data);
            Assert.Empty(data.Members);

            var version = data.Version;
            foreach (var membershipEntry in membershipEntries)
            {
                Assert.True(await membershipTable.InsertRowAsync(membershipEntry, version.Next(), cancellationToken));
                version = (await membershipTable.ReadRowAsync(membershipEntry.SiloAddress, cancellationToken)).Version;
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
            var cancellationToken = TestContext.Current.CancellationToken;
            var data = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.NotNull(data);

            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Empty(data.Members);
            Assert.NotNull(data.Version.VersionEtag);
            Assert.Equal(0, data.Version.Version);
        }

        protected async Task MembershipTable_InsertRow(bool extendedProtocol = true)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipEntry = CreateMembershipEntryForTest();

            var data = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.NotNull(data);
            Assert.Empty(data.Members);

            TableVersion nextTableVersion = data.Version.Next();

            bool ok = await membershipTable.InsertRowAsync(membershipEntry, nextTableVersion, cancellationToken);
            Assert.True(ok, "InsertRow failed");

            data = await membershipTable.ReadAllAsync(cancellationToken);

            if (extendedProtocol)
                Assert.Equal(1, data.Version.Version);

            Assert.Single(data.Members);
        }

        protected async Task MembershipTable_ReadRow_Insert_Read(bool extendedProtocol = true)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            MembershipTableData data = await membershipTable.ReadAllAsync(cancellationToken);

            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Empty(data.Members);

            TableVersion newTableVersion = data.Version.Next();

            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, cancellationToken);

            Assert.True(ok, "InsertRow failed");

            ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, cancellationToken);
            Assert.False(ok, "InsertRow should have failed - same entry, old table version");

            if (extendedProtocol)
            {
                ok = await membershipTable.InsertRowAsync(CreateMembershipEntryForTest(), newTableVersion, cancellationToken);
                Assert.False(ok, "InsertRow should have failed - new entry, old table version");
            }

            data = await membershipTable.ReadAllAsync(cancellationToken);

            if (extendedProtocol)
                Assert.Equal(1, data.Version.Version);

            TableVersion nextTableVersion = data.Version.Next();

            ok = await membershipTable.InsertRowAsync(newEntry, nextTableVersion, cancellationToken);
            Assert.False(ok, "InsertRow should have failed - duplicate entry");

            data = await membershipTable.ReadAllAsync(cancellationToken);

            Assert.Single(data.Members);

            data = await membershipTable.ReadRowAsync(newEntry.SiloAddress, cancellationToken);
            if (extendedProtocol)
                Assert.Equal(newTableVersion.Version, data.Version.Version);

            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Single(data.Members);
            Assert.NotNull(data.Version.VersionEtag);
            if (extendedProtocol)
            {
                Assert.NotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag);
                Assert.Equal(newTableVersion.Version, data.Version.Version);
            }
            var membershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.LogInformation("Membership.ReadRow returned MembershipEntry ETag={ETag} Entry={Entry}", eTag, membershipEntry);

            Assert.NotNull(eTag);
            Assert.NotNull(membershipEntry);
        }

        protected async Task MembershipTable_ReadAll_Insert_ReadAll(bool extendedProtocol = true)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            MembershipTableData data = await membershipTable.ReadAllAsync(cancellationToken);
            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Empty(data.Members);

            TableVersion newTableVersion = data.Version.Next();

            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, cancellationToken);

            Assert.True(ok, "InsertRow failed");

            data = await membershipTable.ReadAllAsync(cancellationToken);
            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Single(data.Members);
            Assert.NotNull(data.Version.VersionEtag);

            if (extendedProtocol)
            {
                Assert.NotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag);
                Assert.Equal(newTableVersion.Version, data.Version.Version);
            }

            var membershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.LogInformation("Membership.ReadAll returned MembershipEntry ETag={ETag} Entry={Entry}", eTag, membershipEntry);

            Assert.NotNull(eTag);
            Assert.NotNull(membershipEntry);
        }

        protected async Task MembershipTable_UpdateRow(bool extendedProtocol = true)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tableData = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.NotNull(tableData.Version);

            Assert.Equal(0, tableData.Version.Version);
            Assert.Empty(tableData.Members);

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

                logger.LogInformation("Calling InsertRow with Entry = {Entry} TableVersion = {TableVersion}", siloEntry, tableVersion);
                bool ok = await membershipTable.InsertRowAsync(siloEntry, tableVersion, cancellationToken);

                Assert.True(ok, "InsertRow failed");

                tableData = await membershipTable.ReadAllAsync(cancellationToken);

                var etagBefore = tableData.TryGet(siloEntry.SiloAddress)?.Item2;

                Assert.NotNull(etagBefore);

                if (extendedProtocol)
                {
                    logger.LogInformation(
                        "Calling UpdateRow with Entry = {Entry} correct eTag = {ETag} old version={TableVersion}",
                        siloEntry,
                        etagBefore,
                        tableVersion?.ToString() ?? "null");
                    ok = await membershipTable.UpdateRowAsync(siloEntry, etagBefore, tableVersion!, cancellationToken);
                    Assert.False(ok, $"row update should have failed - Table Data = {tableData}");
                    tableData = await membershipTable.ReadAllAsync(cancellationToken);
                }

                tableVersion = tableData.Version.Next();

                logger.LogInformation(
                    "Calling UpdateRow with Entry = {Entry} correct eTag = {ETag} correct version={TableVersion}",
                    siloEntry,
                    etagBefore,
                    tableVersion?.ToString() ?? "null");

                ok = await membershipTable.UpdateRowAsync(siloEntry, etagBefore, tableVersion!, cancellationToken);

                Assert.True(ok, $"UpdateRow failed - Table Data = {tableData}");

                logger.LogInformation(
                    "Calling UpdateRow with Entry = {Entry} old eTag = {ETag} old version={TableVersion}",
                    siloEntry,
                    etagBefore,
                    tableVersion?.ToString() ?? "null");
                ok = await membershipTable.UpdateRowAsync(siloEntry, etagBefore, tableVersion!, cancellationToken);
                Assert.False(ok, $"row update should have failed - Table Data = {tableData}");

                tableData = await membershipTable.ReadAllAsync(cancellationToken);

                var tuple = tableData.TryGet(siloEntry.SiloAddress);

                Assert.NotNull(tuple);
                Assert.Equal(tuple.Item1.ToFullString(), siloEntry.ToFullString());

                var etagAfter = tuple.Item2;

                if (extendedProtocol)
                {
                    logger.LogInformation(
                        "Calling UpdateRow with Entry = {Entry} correct eTag = {ETag} old version={TableVersion}",
                        siloEntry,
                        etagAfter,
                        tableVersion?.ToString() ?? "null");

                    ok = await membershipTable.UpdateRowAsync(siloEntry, etagAfter, tableVersion!, cancellationToken);

                    Assert.False(ok, $"row update should have failed - Table Data = {tableData}");
                }

                tableData = await membershipTable.ReadAllAsync(cancellationToken);

                etagBefore = etagAfter;

                etagAfter = tableData.TryGet(siloEntry.SiloAddress)?.Item2;

                Assert.Equal(etagBefore, etagAfter);
                Assert.NotNull(tableData.Version);
                if (extendedProtocol)
                    Assert.Equal(tableVersion!.Version, tableData.Version.Version);

                Assert.Equal(i, tableData.Members.Count);
            }
        }

        protected async Task MembershipTable_UpdateRowInParallel(bool extendedProtocol = true)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var tableData = await membershipTable.ReadAllAsync(cancellationToken);

            var data = CreateMembershipEntryForTest();

            TableVersion newTableVer = tableData.Version.Next();

            var insertions = Task.WhenAll(Enumerable.Range(1, 20).Select(async i => { try { return await membershipTable.InsertRowAsync(data, newTableVer, cancellationToken); } catch { return false; } }));

            Assert.True((await insertions).Single(x => x), "InsertRow failed");

            await Task.WhenAll(Enumerable.Range(1, 19).Select(async i =>
            {
                var done = false;
                do
                {
                    var updatedTableData = await membershipTable.ReadAllAsync(cancellationToken);
                    var updatedRow = updatedTableData.TryGet(data.SiloAddress);

                    await Task.Delay(10, cancellationToken);
                    if (updatedRow is null) continue;

                    TableVersion tableVersion = updatedTableData.Version.Next();
                    try
                    {
                        done = await membershipTable.UpdateRowAsync(updatedRow.Item1, updatedRow.Item2, tableVersion, cancellationToken);
                    }
                    catch
                    {
                        done = false;
                    }
                } while (!done);
            })).WaitAsync(TimeSpan.FromSeconds(30));


            tableData = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.NotNull(tableData.Version);

            if (extendedProtocol)
                Assert.Equal(20, tableData.Version.Version);

            Assert.Single(tableData.Members);
        }

        protected async Task MembershipTable_UpdateIAmAlive(bool extendedProtocol = true)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            MembershipTableData tableData = await membershipTable.ReadAllAsync(cancellationToken);

            TableVersion newTableVersion = tableData.Version.Next();
            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, cancellationToken);
            Assert.True(ok);

            var amAliveTime = DateTime.UtcNow.Add(TimeSpan.FromSeconds(5));

            // This mimics the arguments MembershipOracle.OnIAmAliveUpdateInTableTimer passes in
            var entry = new MembershipEntry
            {
                SiloAddress = newEntry.SiloAddress,
                IAmAliveTime = amAliveTime
            };

            await membershipTable.UpdateIAmAliveAsync(entry, cancellationToken);

            tableData = await membershipTable.ReadAllAsync(cancellationToken);
            var member = tableData.Members.First(e => e.Item1.SiloAddress.Equals(newEntry.SiloAddress));

            // compare that the value is close to what we passed in, but not exactly, as the underlying store can set its own precision settings
            // (ie: in SQL Server this is defined as datetime2(3), so we don't expect precision to account for less than 0.001s values)
            Assert.True((amAliveTime - member.Item1.IAmAliveTime).Duration() < TimeSpan.FromSeconds(2), (amAliveTime - member.Item1.IAmAliveTime).Duration().ToString());
            Assert.Equal(newTableVersion.Version, tableData.Version.Version);
        }

        protected async Task MembershipTable_CleanupDefunctSiloEntries(bool extendedProtocol = true)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            MembershipTableData data = await membershipTable.ReadAllAsync(cancellationToken);
            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Empty(data.Members);

            TableVersion newTableVersion = data.Version.Next();

            var oldEntryDead = CreateMembershipEntryForTest();
            oldEntryDead.IAmAliveTime = oldEntryDead.IAmAliveTime.AddDays(-10);
            oldEntryDead.StartTime = oldEntryDead.StartTime.AddDays(-10);
            oldEntryDead.Status = SiloStatus.Dead;
            bool ok = await membershipTable.InsertRowAsync(oldEntryDead, newTableVersion, cancellationToken);
            var table = await membershipTable.ReadAllAsync(cancellationToken);

            Assert.True(ok, "InsertRow Dead failed");

            newTableVersion = table.Version.Next();
            var oldEntryJoining = CreateMembershipEntryForTest();
            oldEntryJoining.IAmAliveTime = oldEntryJoining.IAmAliveTime.AddDays(-10);
            oldEntryJoining.StartTime = oldEntryJoining.StartTime.AddDays(-10);
            oldEntryJoining.Status = SiloStatus.Joining;
            ok = await membershipTable.InsertRowAsync(oldEntryJoining, newTableVersion, cancellationToken);
            table = await membershipTable.ReadAllAsync(cancellationToken);

            Assert.True(ok, "InsertRow Joining failed");

            newTableVersion = table.Version.Next();
            var newEntry = CreateMembershipEntryForTest();
            ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, cancellationToken);

            Assert.True(ok, "InsertRow failed");

            data = await membershipTable.ReadAllAsync(cancellationToken);
            newTableVersion = data.Version.Next();
            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Equal(3, data.Members.Count);

            // Every status other than Active should get cleared out if old
            foreach (var siloStatus in Enum.GetValues<SiloStatus>())
            {
                var oldEntry = CreateMembershipEntryForTest();
                oldEntry.IAmAliveTime = oldEntry.IAmAliveTime.AddDays(-10);
                oldEntry.StartTime = oldEntry.StartTime.AddDays(-10);
                oldEntry.Status = siloStatus;
                ok = await membershipTable.InsertRowAsync(oldEntry, newTableVersion, cancellationToken);
                table = await membershipTable.ReadAllAsync(cancellationToken);

                Assert.True(ok, "InsertRow failed");

                newTableVersion = table.Version.Next();
            }

            await membershipTable.CleanupDefunctSiloEntriesAsync(oldEntryDead.IAmAliveTime.AddDays(3), cancellationToken);

            data = await membershipTable.ReadAllAsync(cancellationToken);
            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Equal(2, data.Members.Count);
        }

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
