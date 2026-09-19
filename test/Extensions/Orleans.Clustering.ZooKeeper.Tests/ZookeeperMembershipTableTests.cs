using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Clustering.TestKit;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.Configuration;
using org.apache.zookeeper;
using org.apache.utils;
using TestExtensions;
using Xunit;
using Tester.ZooKeeperUtils;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans SiloInstanceManager using ZookeeperStore - Requires access to external Zookeeper storage
    /// 
    /// Apache ZooKeeper provides a hierarchical namespace for distributed coordination.
    /// Orleans uses ZooKeeper for:
    /// - Distributed membership management using znodes (ZooKeeper nodes)
    /// - Leader election and distributed consensus
    /// - Ephemeral nodes for automatic cleanup on silo failure
    /// - Watch notifications for membership changes
    /// 
    /// These tests verify the ZooKeeper-based membership provider handles all
    /// membership operations correctly, including node failures and network partitions.
    /// </summary>
    [TestCategory("Membership"), TestCategory("ZooKeeper")]
    [TestSuite("Functional")]
    [TestProvider("ZooKeeper")]
    [TestArea("Membership")]
    public class ZookeeperMembershipTableTests : MembershipTableTestsBase, IAsyncLifetime
    {
        private readonly NativeSocketDiagnostics _socketDiagnostics = new();

        static ZookeeperMembershipTableTests()
        {
            ZooKeeper.LogLevel = TraceLevel.Info;
            ZooKeeper.LogToTrace = false;
            ZooKeeper.CustomLogConsumer = new SdkDiagnostics();
        }

        public ZookeeperMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
            : base(fixture, environment, CreateFilters())
        {
        }

        public new async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                _socketDiagnostics.Dispose();
            }
        }

        private static LoggerFilterOptions CreateFilters()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter(typeof(ZookeeperMembershipTableTests).Name, LogLevel.Trace);
            return filters;
        }

        /// <summary>
        /// Creates a ZooKeeper-based membership table for testing.
        /// Configures the ZooKeeper connection and creates the membership
        /// table that uses ZooKeeper's hierarchical namespace for storage.
        /// </summary>
        protected override IMembershipTable CreateMembershipTable(ILogger logger)
            => CreateMembershipTable(logger, _clusterOptions);

        protected override IMembershipTable CreateMembershipTable(ILogger logger, IOptions<ClusterOptions> clusterOptions)
        {
            var options = new ZooKeeperClusteringSiloOptions();
            options.ConnectionString = this.connectionString;

            var typedLogger = this.Services.GetService<ILogger<ZooKeeperBasedMembershipTable>>();
            Assert.NotNull(typedLogger);
            return new ZooKeeperBasedMembershipTable(typedLogger, Options.Create(options), clusterOptions);
        }

        protected override MembershipTableTestFixture CreateConformanceFixture()
            => CreateConformanceFixture(IsConformanceClusterDeletedAsync);

        private async ValueTask<bool> IsConformanceClusterDeletedAsync(string clusterId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ZooKeeper.Using(connectionString, 10_000, new ConformanceWatcher(), async client =>
            {
                await client.sync("/");
                cancellationToken.ThrowIfCancellationRequested();
                return await client.existsAsync("/" + clusterId, false) is null;
            });
        }

        private sealed class ConformanceWatcher : Watcher
        {
            public override Task process(WatchedEvent @event) => Task.CompletedTask;
        }

        internal sealed class NativeSocketDiagnostics(Action<string>? write = null) : EventListener
        {
            private const int MaxLoggedEvents = 256;
            private readonly Action<string> _write = write ?? Console.Error.WriteLine;
            private int _eventCount;

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (eventSource.Name == "Private.InternalDiagnostics.System.Net.Sockets")
                {
                    // Native completion errors precede the SDK's own SocketError assignment.
                    EnableEvents(eventSource, EventLevel.Error, (EventKeywords)1);
                    _write($"{DateTime.UtcNow:O} [NativeSockets#{GetHashCode()}] Error listener enabled");
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                if (eventData.EventName is "ErrorMessage" or "EventSourceMessage")
                {
                    var count = Interlocked.Increment(ref _eventCount);
                    if (count <= MaxLoggedEvents)
                    {
                        _write($"{DateTime.UtcNow:O} [NativeSockets#{GetHashCode()}] {eventData.EventName}: {string.Join("; ", eventData.Payload!)}");
                    }
                    else if (count == MaxLoggedEvents + 1)
                    {
                        _write($"{DateTime.UtcNow:O} [NativeSockets#{GetHashCode()}] Event limit reached; subsequent event details are omitted");
                    }
                }
            }

            public override void Dispose()
            {
                base.Dispose();
                _write($"{DateTime.UtcNow:O} [NativeSockets#{GetHashCode()}] Listener disposed; observed-events={Volatile.Read(ref _eventCount)}; detail-limit={MaxLoggedEvents}");
            }
        }

        private sealed class SdkDiagnostics : ILogConsumer
        {
            public void Log(TraceLevel severity, string className, string message, Exception exception)
            {
                // The SDK reports transport exceptions at Info, below its default Warning threshold.
                if (exception is not null || severity <= TraceLevel.Warning)
                {
                    Console.Error.WriteLine($"{DateTime.UtcNow:O} [{className}] {severity}: {message}{Environment.NewLine}{exception}");
                }
            }
        }

        /// <summary>
        /// Creates a ZooKeeper-based gateway list provider.
        /// This provider uses ZooKeeper watches to maintain an up-to-date
        /// list of available gateways for client connections.
        /// </summary>
        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
        {
            var options = new ZooKeeperGatewayListProviderOptions();
            options.ConnectionString = this.connectionString;

            return ActivatorUtilities.CreateInstance<ZooKeeperGatewayListProvider>(this.Services, Options.Create(options), this._clusterOptions);
        }

        protected override async Task<string> GetConnectionString()
        {
            bool isReachable = await ZookeeperTestUtils.EnsureZooKeeperAsync(TestContext.Current.CancellationToken);
            return isReachable ? TestDefaultConfiguration.ZooKeeperConnectionString! : null!;
        }

        [Fact]
        public Task MembershipTable_ZooKeeper_Init()
            => InitializeLegacyMembershipTableAsync(TestContext.Current.CancellationToken);

        [Fact]
        public async Task MembershipTable_ZooKeeper_GetGateways()
        {
            await MembershipTable_GetGateways();
        }

        [Fact]
        public async Task MembershipTable_ZooKeeper_ReadAll_EmptyTable()
        {
            await MembershipTable_ReadAll_EmptyTable();
        }

        /// <summary>
        /// Tests inserting a silo entry as a ZooKeeper znode.
        /// Verifies that the membership data is correctly serialized
        /// and stored in ZooKeeper's hierarchical structure.
        /// </summary>
        [Fact]
        public async Task MembershipTable_ZooKeeper_InsertRow()
        {
            await MembershipTable_InsertRow();
        }

        [Fact]
        public async Task MembershipTable_ZooKeeper_ReadRow_Insert_Read()
        {
            await MembershipTable_ReadRow_Insert_Read();
        }

        [Fact]
        public async Task MembershipTable_ZooKeeper_ReadAll_Insert_ReadAll()
        {
            await MembershipTable_ReadAll_Insert_ReadAll();
        }

        [Fact]
        public async Task MembershipTable_ZooKeeper_UpdateRow()
        {
            await MembershipTable_UpdateRow();
        }

        /// <summary>
        /// Tests concurrent updates using ZooKeeper's versioning.
        /// Verifies that ZooKeeper's optimistic concurrency control
        /// correctly handles simultaneous updates from multiple silos.
        /// </summary>
        [Fact]
        public async Task MembershipTable_ZooKeeper_UpdateRowInParallel()
        {
            await MembershipTable_UpdateRowInParallel();
        }

        /// <summary>
        /// Tests heartbeat updates using ZooKeeper.
        /// Verifies that ephemeral nodes and session timeouts
        /// work correctly for detecting failed silos.
        /// </summary>
        [Fact]
        public async Task MembershipTable_ZooKeeper_UpdateIAmAlive()
        {
            await MembershipTable_UpdateIAmAlive();
        }
    }
}
