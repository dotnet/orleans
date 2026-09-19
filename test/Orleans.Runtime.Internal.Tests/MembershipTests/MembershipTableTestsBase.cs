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
using Orleans.Clustering.TestKit;

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
    [TestCategory("Functional")]
    public abstract class MembershipTableTestsBase : MembershipTableFullConformanceTestsBase, IAsyncLifetime, IClassFixture<ConnectionStringFixture>
    {
        private static readonly string hostName = Dns.GetHostName();
        private readonly ILogger logger;
        private readonly object _legacyLock = new();
        private Task<IMembershipTable>? _legacyMembershipInitialization;
        private Task<IGatewayListProvider>? _legacyGatewayInitialization;
        private MembershipTableTestHandle? _legacyMembershipHandle;
        private IGatewayListProvider? _legacyGateway;
        private Task? _disposal;
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

            try
            {
                fixture.InitializeConnectionStringAccessor(GetConnectionString);
                this.connectionString = fixture.ConnectionString;
                if (string.IsNullOrEmpty(this.connectionString))
                {
                    throw Xunit.Sdk.SkipException.ForSkip("No connection string configured");
                }

                this._clusterOptions = Options.Create(new ClusterOptions { ClusterId = this.clusterId });
                this._gatewayOptions = Options.Create(new GatewayOptions());
            }
            catch
            {
                loggerFactory.Dispose();
                throw;
            }
        }

        public IGrainFactory GrainFactory => this.environment.GrainFactory;

        public IServiceProvider Services => this.environment.Services;

        public ValueTask InitializeAsync() => ValueTask.CompletedTask;

        /// <summary>Explicitly initializes the legacy table without creating a gateway provider.</summary>
        protected async Task InitializeLegacyMembershipTableAsync(CancellationToken cancellationToken)
            => _ = await GetLegacyMembershipTableAsync(cancellationToken);

        /// <summary>Gets this test's single, lazily initialized legacy membership table.</summary>
        protected Task<IMembershipTable> GetLegacyMembershipTableAsync(CancellationToken cancellationToken)
        {
            lock (_legacyLock)
            {
                ObjectDisposedException.ThrowIf(_disposal is not null, this);
                cancellationToken.ThrowIfCancellationRequested();
                return _legacyMembershipInitialization ??= InitializeLegacyMembershipTableCoreAsync(cancellationToken);
            }
        }

        private async Task<IMembershipTable> InitializeLegacyMembershipTableCoreAsync(CancellationToken cancellationToken)
        {
            var handle = CreateLegacyMembershipTableHandle(logger);
            try
            {
                using var initialization = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                initialization.CancelAfter(SiloInstanceTableTestConstants.Timeout);
                await handle.Table.InitializeMembershipTableAsync(true, initialization.Token);
                _legacyMembershipHandle = handle;
                return handle.Table;
            }
            catch (Exception initializationFailure)
            {
                try
                {
                    await handle.DisposeAsync();
                }
                catch (Exception disposalFailure)
                {
                    throw new AggregateException(initializationFailure, disposalFailure);
                }

                throw;
            }
        }

        /// <summary>Gets the legacy gateway provider after initializing its actual membership table.</summary>
        protected Task<IGatewayListProvider> GetLegacyGatewayListProviderAsync(CancellationToken cancellationToken)
        {
            lock (_legacyLock)
            {
                ObjectDisposedException.ThrowIf(_disposal is not null, this);
                cancellationToken.ThrowIfCancellationRequested();
                return _legacyGatewayInitialization ??= InitializeLegacyGatewayListProviderCoreAsync(cancellationToken);
            }
        }

        private async Task<IGatewayListProvider> InitializeLegacyGatewayListProviderCoreAsync(CancellationToken cancellationToken)
        {
            var table = await GetLegacyMembershipTableAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var gateway = CreateGatewayListProvider(logger, table);
            try
            {
                // The gateway API has no cancellation parameter: await its returned operation before releasing it.
                await gateway.InitializeGatewayListProvider();
                _legacyGateway = gateway;
                return gateway;
            }
            catch (Exception initializationFailure)
            {
                try
                {
                    await DisposeLegacyGatewayListProviderAsync(gateway);
                }
                catch (Exception disposalFailure)
                {
                    throw new AggregateException(initializationFailure, disposalFailure);
                }

                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_legacyLock)
            {
                return new ValueTask(_disposal ??= DisposeLegacyResourcesAsync());
            }
        }

        private async Task DisposeLegacyResourcesAsync()
        {
            var failures = new List<Exception>();
            // Initializers clean up their own failures. Only outstanding operations need joining here.
            foreach (var initialization in new Task?[] { _legacyMembershipInitialization, _legacyGatewayInitialization })
            {
                if (initialization is { IsCompleted: false })
                {
                    try { await initialization; }
                    catch (Exception exception) { failures.Add(exception); }
                }
            }

            if (_legacyMembershipHandle is { } handle)
            {
                if (SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
                {
                    using var cleanup = new CancellationTokenSource(SiloInstanceTableTestConstants.Timeout);
                    try { await handle.Table.DeleteMembershipTableEntriesAsync(clusterId, cleanup.Token); }
                    catch (Exception exception) { failures.Add(exception); }
                }
            }

            if (_legacyGateway is { } gateway)
            {
                try { await DisposeLegacyGatewayListProviderAsync(gateway); }
                catch (Exception exception) { failures.Add(exception); }
            }

            if (_legacyMembershipHandle is { } membershipHandle)
            {
                try { await membershipHandle.DisposeAsync(); }
                catch (Exception exception) { failures.Add(exception); }
            }

            try { loggerFactory.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            if (failures.Count > 0)
            {
                throw new AggregateException("Legacy membership fixture teardown failed.", failures);
            }
        }

        /// <summary>Creates the legacy provider and owns any additional native SDK resources it needs.</summary>
        protected virtual MembershipTableTestHandle CreateLegacyMembershipTableHandle(ILogger logger)
        {
            var table = CreateMembershipTable(logger);
            return new MembershipTableTestHandle(table, () => DisposeResourceAsync(table));
        }

        /// <summary>Creates a gateway provider using the already initialized legacy membership table when needed.</summary>
        protected virtual IGatewayListProvider CreateGatewayListProvider(ILogger logger, IMembershipTable membershipTable)
            => CreateGatewayListProvider(logger);

        /// <summary>Releases a constructed legacy gateway provider and any adapter-owned native resources.</summary>
        protected virtual ValueTask DisposeLegacyGatewayListProviderAsync(IGatewayListProvider gatewayListProvider)
            => DisposeResourceAsync(gatewayListProvider);

        private static ValueTask DisposeResourceAsync(object resource)
        {
            if (resource is IAsyncDisposable asyncDisposable)
            {
                return asyncDisposable.DisposeAsync();
            }

            (resource as IDisposable)?.Dispose();
            return ValueTask.CompletedTask;
        }

        protected abstract IGatewayListProvider CreateGatewayListProvider(ILogger logger);
        protected abstract IMembershipTable CreateMembershipTable(ILogger logger);
        protected abstract IMembershipTable CreateMembershipTable(ILogger logger, IOptions<ClusterOptions> clusterOptions);
        protected abstract Task<string> GetConnectionString();

        protected MembershipTableTestFixture CreateConformanceFixture(Func<string, CancellationToken, ValueTask<bool>> isDeletedAsync)
            => new(GetType().Name, (serviceId, clusterId, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(CreateConformanceHandle(
                    logger,
                    Options.Create(new ClusterOptions { ServiceId = serviceId, ClusterId = clusterId })));
            }, isDeletedAsync);

        protected virtual MembershipTableTestHandle CreateConformanceHandle(ILogger logger, IOptions<ClusterOptions> clusterOptions)
        {
            var table = CreateMembershipTable(logger, clusterOptions);
            return new MembershipTableTestHandle(table, () => DisposeResourceAsync(table));
        }

        protected virtual string? GetAdoInvariant()
        {
            return null;
        }

        protected async Task MembershipTable_GetGateways()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var gatewayListProvider = await GetLegacyGatewayListProviderAsync(cancellationToken);
            var membershipTable = await GetLegacyMembershipTableAsync(cancellationToken);
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
            var membershipTable = await GetLegacyMembershipTableAsync(cancellationToken);
            var data = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.NotNull(data);

            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Empty(data.Members);
            Assert.NotNull(data.Version.VersionEtag);
            Assert.Equal(0, data.Version.Version);
        }

        protected async Task MembershipTable_InsertRow()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = await GetLegacyMembershipTableAsync(cancellationToken);
            var membershipEntry = CreateMembershipEntryForTest();

            var data = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.NotNull(data);
            Assert.Empty(data.Members);

            TableVersion nextTableVersion = data.Version.Next();

            bool ok = await membershipTable.InsertRowAsync(membershipEntry, nextTableVersion, cancellationToken);
            Assert.True(ok, "InsertRow failed");

            data = await membershipTable.ReadAllAsync(cancellationToken);

            Assert.Equal(1, data.Version.Version);

            Assert.Single(data.Members);
        }

        protected async Task MembershipTable_ReadRow_Insert_Read()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = await GetLegacyMembershipTableAsync(cancellationToken);
            MembershipTableData data = await membershipTable.ReadAllAsync(cancellationToken);

            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Empty(data.Members);

            TableVersion newTableVersion = data.Version.Next();

            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, cancellationToken);

            Assert.True(ok, "InsertRow failed");

            ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, cancellationToken);
            Assert.False(ok, "InsertRow should have failed - same entry, old table version");

            ok = await membershipTable.InsertRowAsync(CreateMembershipEntryForTest(), newTableVersion, cancellationToken);
            Assert.False(ok, "InsertRow should have failed - new entry, old table version");

            data = await membershipTable.ReadAllAsync(cancellationToken);

            Assert.Equal(1, data.Version.Version);

            TableVersion nextTableVersion = data.Version.Next();

            ok = await membershipTable.InsertRowAsync(newEntry, nextTableVersion, cancellationToken);
            Assert.False(ok, "InsertRow should have failed - duplicate entry");

            data = await membershipTable.ReadAllAsync(cancellationToken);

            Assert.Single(data.Members);

            data = await membershipTable.ReadRowAsync(newEntry.SiloAddress, cancellationToken);
            Assert.Equal(newTableVersion.Version, data.Version.Version);

            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Single(data.Members);
            Assert.NotNull(data.Version.VersionEtag);
            Assert.NotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag);
            Assert.Equal(newTableVersion.Version, data.Version.Version);
            var membershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.LogInformation("Membership.ReadRow returned MembershipEntry ETag={ETag} Entry={Entry}", eTag, membershipEntry);

            Assert.NotNull(eTag);
            Assert.NotNull(membershipEntry);
        }

        protected async Task MembershipTable_ReadAll_Insert_ReadAll()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = await GetLegacyMembershipTableAsync(cancellationToken);
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

            Assert.NotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag);
            Assert.Equal(newTableVersion.Version, data.Version.Version);

            var membershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.LogInformation("Membership.ReadAll returned MembershipEntry ETag={ETag} Entry={Entry}", eTag, membershipEntry);

            Assert.NotNull(eTag);
            Assert.NotNull(membershipEntry);
        }

        protected async Task MembershipTable_UpdateRow()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = await GetLegacyMembershipTableAsync(cancellationToken);
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

                logger.LogInformation(
                    "Calling UpdateRow with Entry = {Entry} correct eTag = {ETag} old version={TableVersion}",
                    siloEntry,
                    etagBefore,
                    tableVersion?.ToString() ?? "null");
                ok = await membershipTable.UpdateRowAsync(siloEntry, etagBefore, tableVersion!, cancellationToken);
                Assert.False(ok, $"row update should have failed - Table Data = {tableData}");
                tableData = await membershipTable.ReadAllAsync(cancellationToken);

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
                AssertCanonicalEntryEqual(CaptureCanonicalEntry(siloEntry), CaptureCanonicalEntry(tuple.Item1));

                var etagAfter = tuple.Item2;
                var versionBeforeRejectedUpdate = tableData.Version;

                logger.LogInformation(
                    "Calling UpdateRow with Entry = {Entry} correct eTag = {ETag} old version={TableVersion}",
                    siloEntry,
                    etagAfter,
                    tableVersion?.ToString() ?? "null");

                ok = await membershipTable.UpdateRowAsync(siloEntry, etagAfter, tableVersion!, cancellationToken);

                Assert.False(ok, $"row update should have failed - Table Data = {tableData}");

                tableData = await membershipTable.ReadAllAsync(cancellationToken);

                Assert.NotNull(tableData.Version);
                Assert.Equal(versionBeforeRejectedUpdate, tableData.Version);
                Assert.Equal(tableVersion!.Version, tableData.Version.Version);

                Assert.Equal(i, tableData.Members.Count);
            }

            var beforeMissingUpdate = tableData;
            var expectedRows = beforeMissingUpdate.Members.Select(row => CaptureCanonicalEntry(row.Item1)).ToArray();
            Assert.False(await membershipTable.UpdateRowAsync(
                CreateMembershipEntryForTest(), beforeMissingUpdate.Members[0].Item2,
                beforeMissingUpdate.Version.Next(), cancellationToken));
            tableData = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.Equal(beforeMissingUpdate.Version, tableData.Version);
            AssertCanonicalEntriesEqual(expectedRows, tableData.Members.Select(row => CaptureCanonicalEntry(row.Item1)));
        }

        protected async Task MembershipTable_UpdateRowInParallel()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = await GetLegacyMembershipTableAsync(cancellationToken);
            using var workersCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            workersCancellation.CancelAfter(TimeSpan.FromSeconds(30));
            var workerToken = workersCancellation.Token;
            var tableData = await membershipTable.ReadAllAsync(cancellationToken);

            var data = CreateMembershipEntryForTest();

            TableVersion newTableVer = tableData.Version.Next();

            var insertions = Task.WhenAll(Enumerable.Range(1, 20).Select(async _ =>
                await membershipTable.InsertRowAsync(data, newTableVer, workerToken)));

            Assert.True((await insertions).Single(x => x), "InsertRow failed");

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workers = Enumerable.Range(1, 19).Select(async _ =>
            {
                try
                {
                    await start.Task;
                    while (true)
                    {
                        workerToken.ThrowIfCancellationRequested();
                        var updatedTableData = await membershipTable.ReadAllAsync(workerToken);
                        var updatedRow = updatedTableData.TryGet(data.SiloAddress);
                        Assert.NotNull(updatedRow);
                        if (await membershipTable.UpdateRowAsync(
                            updatedRow.Item1, updatedRow.Item2, updatedTableData.Version.Next(), workerToken))
                        {
                            return;
                        }

                        await Task.Yield();
                    }
                }
                catch
                {
                    workersCancellation.Cancel();
                    throw;
                }
            }).ToArray();
            start.SetResult();
            await Task.WhenAll(workers);

            tableData = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.NotNull(tableData.Version);

            Assert.Equal(20, tableData.Version.Version);

            Assert.Single(tableData.Members);
        }

        protected async Task MembershipTable_UpdateIAmAlive()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = await GetLegacyMembershipTableAsync(cancellationToken);
            MembershipTableData tableData = await membershipTable.ReadAllAsync(cancellationToken);

            TableVersion newTableVersion = tableData.Version.Next();
            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, cancellationToken);
            Assert.True(ok);

            var beforeHeartbeat = await membershipTable.ReadAllAsync(cancellationToken);
            var originalRow = Assert.Single(beforeHeartbeat.Members);
            var originalCanonicalEntry = CaptureCanonicalEntry(originalRow.Item1);
            var amAliveTime = DateTime.UtcNow.Add(TimeSpan.FromSeconds(5));

            // This mimics the arguments MembershipOracle.OnIAmAliveUpdateInTableTimer passes in
            var entry = new MembershipEntry
            {
                SiloAddress = newEntry.SiloAddress,
                IAmAliveTime = amAliveTime
            };

            await membershipTable.UpdateIAmAliveAsync(entry, cancellationToken);

            tableData = await membershipTable.ReadAllAsync(cancellationToken);
            AssertCanonicalEntryEqual(originalCanonicalEntry, CaptureCanonicalEntry(Assert.Single(tableData.Members).Item1));
            Assert.Equal(beforeHeartbeat.Version, tableData.Version);

            // Heartbeat-only physical changes must not reject a canonical update using the original inputs.
            newEntry.Status = SiloStatus.Active;
            Assert.True(await membershipTable.UpdateRowAsync(
                newEntry, originalRow.Item2, beforeHeartbeat.Version.Next(), cancellationToken));
            tableData = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.Equal(beforeHeartbeat.Version.Version + 1, tableData.Version.Version);
            Assert.NotEqual(beforeHeartbeat.Version.VersionEtag, tableData.Version.VersionEtag);
            AssertCanonicalEntryEqual(CaptureCanonicalEntry(newEntry), CaptureCanonicalEntry(Assert.Single(tableData.Members).Item1));
        }

        protected async Task MembershipTable_CleanupDefunctSiloEntries()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = await GetLegacyMembershipTableAsync(cancellationToken);
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
            var oldEntryActive = CreateMembershipEntryForTest();
            oldEntryActive.IAmAliveTime = oldEntryActive.IAmAliveTime.AddDays(-10);
            oldEntryActive.StartTime = oldEntryActive.StartTime.AddDays(-10);
            oldEntryActive.Status = SiloStatus.Active;
            ok = await membershipTable.InsertRowAsync(oldEntryActive, newTableVersion, cancellationToken);
            table = await membershipTable.ReadAllAsync(cancellationToken);

            Assert.True(ok, "InsertRow Active failed");

            newTableVersion = table.Version.Next();
            var newEntry = CreateMembershipEntryForTest();
            newEntry.Status = SiloStatus.Active;
            ok = await membershipTable.InsertRowAsync(newEntry, newTableVersion, cancellationToken);

            Assert.True(ok, "InsertRow failed");

            data = await membershipTable.ReadAllAsync(cancellationToken);
            newTableVersion = data.Version.Next();
            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Equal(3, data.Members.Count);

            foreach (var siloStatus in Enum.GetValues<SiloStatus>().Where(status => status != SiloStatus.None))
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

            var beforeCleanup = await membershipTable.ReadAllAsync(cancellationToken);
            var expected = beforeCleanup.Members.Where(row => row.Item1.Status != SiloStatus.Dead)
                .Select(row => CaptureCanonicalEntry(row.Item1)).ToArray();
            await membershipTable.CleanupDefunctSiloEntriesAsync(oldEntryDead.IAmAliveTime.AddDays(3), cancellationToken);

            data = await membershipTable.ReadAllAsync(cancellationToken);
            logger.LogInformation("Membership.ReadAll returned TableVersion={TableVersion} Data={Data}", data.Version, data);

            Assert.Equal(expected.Length, data.Members.Count);
            Assert.True(data.Version.Version >= beforeCleanup.Version.Version);
            Assert.NotEmpty(data.Version.VersionEtag);
            if (data.Version.Version == beforeCleanup.Version.Version)
            {
                Assert.Equal(beforeCleanup.Version.VersionEtag, data.Version.VersionEtag);
            }

            var afterCleanup = data.Members.Select(row => CaptureCanonicalEntry(row.Item1)).ToArray();
            AssertCanonicalEntriesEqual(expected, afterCleanup);
            await membershipTable.CleanupDefunctSiloEntriesAsync(oldEntryDead.IAmAliveTime.AddDays(3), cancellationToken);
            var repeated = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.Equal(data.Version, repeated.Version);
            AssertCanonicalEntriesEqual(afterCleanup, repeated.Members.Select(row => CaptureCanonicalEntry(row.Item1)));
        }

        // Utility methods
        private sealed record CanonicalFields(
            string Identity, SiloStatus Status, int ProxyPort, string HostName, string SiloName,
            string RoleName, int UpdateZone, int FaultZone, DateTime StartTime);

        private sealed record CanonicalEntry(CanonicalFields Fields, (string Identity, DateTime Time)[] Suspects);

        private static CanonicalEntry CaptureCanonicalEntry(MembershipEntry entry) => new(
            new CanonicalFields(
                entry.SiloAddress.ToParsableString(), entry.Status, entry.ProxyPort, entry.HostName, entry.SiloName,
                entry.RoleName ?? string.Empty, entry.UpdateZone, entry.FaultZone, entry.StartTime),
            (entry.SuspectTimes ?? []).Select(vote => (vote.Item1.ToParsableString(), vote.Item2))
                .OrderBy(vote => vote.Item1, StringComparer.Ordinal).ThenBy(vote => vote.Item2).ToArray());

        private static void AssertCanonicalEntryEqual(CanonicalEntry expected, CanonicalEntry actual)
        {
            Assert.Equal(expected.Fields, actual.Fields);
            Assert.Equal(expected.Suspects, actual.Suspects);
        }

        private static void AssertCanonicalEntriesEqual(IEnumerable<CanonicalEntry> expected, IEnumerable<CanonicalEntry> actual)
        {
            var expectedRows = expected.OrderBy(row => row.Fields.Identity, StringComparer.Ordinal).ToArray();
            var actualRows = actual.OrderBy(row => row.Fields.Identity, StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedRows.Length, actualRows.Length);
            foreach (var (expectedRow, actualRow) in expectedRows.Zip(actualRows))
            {
                AssertCanonicalEntryEqual(expectedRow, actualRow);
            }
        }

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
