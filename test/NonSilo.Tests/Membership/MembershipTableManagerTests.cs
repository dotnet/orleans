using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Xunit;
using NSubstitute;
using Orleans.Runtime;
using System;
using Orleans;
using Xunit.Abstractions;
using System.Linq;
using TestExtensions;
using Newtonsoft.Json;

namespace NonSilo.Tests.Membership
{
    /// <summary>
    /// Tests for <see cref="MembershipTableManager"/>
    /// </summary>
    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipTableManagerTests
    {
        private readonly ITestOutputHelper output;
        public MembershipTableManagerTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public async Task MembershipTableManager_Startup()
        {
            var loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });
            var log = loggerFactory.CreateLogger(nameof(MembershipTableManager_Startup));
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            var localSilo = Silo("127.0.0.1:100@1");
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("MyServer11");
            localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            var membershipTable = new InMemoryMembershipTable();

            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            var membershipGossiper = Substitute.For<IMembershipGossiper>();

            var lifecycle = new SiloLifecycleSubject(loggerFactory.CreateLogger<SiloLifecycleSubject>());

            var manager = new MembershipTableManager(
                localSiloDetails: localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: fatalErrorHandler,
                gossiper: membershipGossiper,
                log: loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: loggerFactory);

            // Validate that the initial snapshot is valid and contains the local silo.
            var initialSnapshot = manager.MembershipTableSnapshot;
            Assert.NotNull(initialSnapshot);
            Assert.NotNull(initialSnapshot.Entries);
            Assert.NotNull(initialSnapshot.LocalSilo);
            Assert.Equal(SiloStatus.Created, initialSnapshot.LocalSilo.Status);
            Assert.Equal(localSiloDetails.Name, initialSnapshot.LocalSilo.SiloName);
            Assert.Equal(localSiloDetails.DnsHostName, initialSnapshot.LocalSilo.HostName);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);

            Assert.NotNull(manager.MembershipTableUpdates);
            var changes = manager.MembershipTableUpdates;
            Assert.Equal(changes.Value.Version, manager.MembershipTableSnapshot.Version);
            Assert.Empty(membershipTable.Calls);

            // All of these checks were performed before any lifecycle methods have a chance to run.
            // This is in order to verify that a service accessing membership in its constructor will
            // see the correct results regardless of initialization order.
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(lifecycle);

            await lifecycle.OnStart();

            var calls = membershipTable.Calls;
            Assert.NotEmpty(calls);
            Assert.Equal(2, calls.Count);
            Assert.Equal(nameof(IMembershipTable.InitializeMembershipTable), calls[0].Method);
            Assert.Equal(nameof(IMembershipTable.ReadAll), calls[1].Method);
            membershipTable.ClearCalls();

            // During initialization, a first read from the table will be performed, transitioning
            // membership from version long.MinValue to version 0 (since it's a mock table here)
            Assert.True(changes.NextAsync().IsCompleted);
            var update1 = changes.NextAsync().GetAwaiter().GetResult();

            // Transition to joining.
            await manager.UpdateStatus(SiloStatus.Joining);
            Assert.Equal(SiloStatus.Joining, manager.CurrentStatus);
            Assert.Equal(SiloStatus.Joining, manager.MembershipTableSnapshot.LocalSilo.Status);

            // An update should have been issued.
            Assert.True(update1.NextAsync().IsCompleted);
            Assert.NotEqual(update1.Value.Version, manager.MembershipTableSnapshot.Version);

            var update2 = update1.NextAsync().GetAwaiter().GetResult();
            Assert.Equal(update2.Value.Version, manager.MembershipTableSnapshot.Version);
            var entry = Assert.Single(update2.Value.Entries);
            Assert.Equal(localSilo, entry.Key);
            Assert.Equal(localSilo, entry.Value.SiloAddress);
            Assert.Equal(SiloStatus.Joining, entry.Value.Status);

            calls = membershipTable.Calls;
            Assert.NotEmpty(calls);
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.ReadAll)));

            await lifecycle.OnStop();
            fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        // Initial snapshots are valid
        // Table refresh:
        // * Does periodic refresh
        // * Quick retry after exception
        // * Emits change notification
        // TrySuspectOrKill tests:
        // Lifecycle tests:
        // * Correct status updates at each level
        // * Verify own memberhip table entry has correct properties
        // * Cleans up old entries for same silo
        // * Graceful & ungraceful shutdown
        // * Timer stalls?
        // * Snapshot updated + change notification emitted after status update
        // Node migration
        // Fault on missing entry during refresh
        // Fault on declared dead
        // Gossips on updates

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status)
        {
            return new MembershipEntry { SiloAddress = address, Status = status };
        }

        private static MembershipTableData Table(params MembershipEntry[] entries)
        {
            var entryList = entries.Select(e => Tuple.Create(e, "test")).ToList();
            return new MembershipTableData(entryList, new TableVersion(12, "test"));
        }
    }

    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipAgentTests
    {
        // Lifecycle tests:
        // * Correct status updates at each level
        // ValidateInitialConnectivity
        // * Enabled vs Disabled
        // UpdateIAmAlive tests
        // * Periodically updated
        // * Correct values
        // * Missing entry
        // * Quick retry after exception
        // Graceful vs ungraceful shutdown
    }

    [TestCategory("BVT"), TestCategory("Membership")]
    public class ClusterHealthMonitorTests
    {
        // Periodically checks health
        // Selects correct subset of silos
        // TrySuspectOrKill on error
        // Graceful vs ungraceful shutdown
        // Processes membership updates
    }

    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipTableCleanupAgentTests
    {
        // Configuration tests:
        // * Clean startup & shutdown when enabled vs disabled
        // Cleans up old, dead entries only
        // Skip cleanup if any entry has timestamp newer than current time?
    }
}
