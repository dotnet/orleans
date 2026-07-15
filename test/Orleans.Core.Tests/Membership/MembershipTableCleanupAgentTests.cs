using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NonSilo.Tests.Utilities;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace NonSilo.Tests.Membership
{
    /// <summary>
    /// Tests for membership table cleanup agent functionality including enabled/disabled scenarios and defunct silo cleanup.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipTableCleanupAgentTests
    {
        private readonly ITestOutputHelper output;
        private readonly LoggerFactory loggerFactory;
        private readonly SiloAddress localSilo;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly FakeTimeProvider timeProvider;

        public MembershipTableCleanupAgentTests(ITestOutputHelper output)
        {
            this.output = output;
            this.loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });
            this.localSilo = Silo("127.0.0.1:100@100");
            this.localSiloDetails = Substitute.For<ILocalSiloDetails>();
            this.localSiloDetails.SiloAddress.Returns(this.localSilo);
            this.timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero));
        }

        [Fact]
        public async Task MembershipTableCleanupAgent_Enabled_BasicScenario()
        {
            await this.BasicScenario(enabled: true);
        }

        [Fact]
        public async Task MembershipTableCleanupAgent_Disabled_BasicScenario()
        {
            await this.BasicScenario(enabled: false);
        }

        [Fact]
        public async Task MembershipTableCleanupAgent_NonFirstActiveSilo_DoesNotCleanup()
        {
            var options = new ClusterMembershipOptions { DefunctSiloCleanupPeriod = TimeSpan.FromMinutes(90), MaxDefunctSiloEntries = null };
            var membershipManager = new TestMembershipManager();
            var cleanupCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var now = this.timeProvider.GetUtcNow();
            var table = new InMemoryMembershipTable
            {
                OnCleanupDefunctSiloEntries = _ => cleanupCalled.TrySetResult()
            };
            var cleanupAgent = this.CreateCleanupAgent(options, table, membershipManager);
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            ((ILifecycleParticipant<ISiloLifecycle>)cleanupAgent).Participate(lifecycle);

            await lifecycle.OnStart();
            membershipManager.Publish(Snapshot(
                Entry(Silo("127.0.0.1:200@1"), SiloStatus.Active, now),
                Entry(this.localSilo, SiloStatus.Active, now)));
            var completed = await Task.WhenAny(cleanupCalled.Task, Task.Delay(TimeSpan.FromMilliseconds(200)));

            Assert.NotSame(cleanupCalled.Task, completed);
            Assert.DoesNotContain(table.Calls, c => c.Method.Equals(nameof(IMembershipTable.CleanupDefunctSiloEntries)));

            await lifecycle.OnStop();
        }

        [Fact]
        public async Task MembershipTableCleanupAgent_ThresholdCleanup_RemovesExcessDefunctEntries()
        {
            var now = this.timeProvider.GetUtcNow();
            var retainedDefunctSilo = Silo("127.0.0.1:700@100");
            var options = new ClusterMembershipOptions { DefunctSiloCleanupPeriod = null, MaxDefunctSiloEntries = 1 };
            var membershipManager = new TestMembershipManager();
            var oldestDefunctEntry = Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead, now.AddMinutes(-3));
            var removedDefunctEntry = Entry(Silo("127.0.0.1:600@100"), SiloStatus.Dead, now.AddMinutes(-2));
            var retainedDefunctEntry = Entry(retainedDefunctSilo, SiloStatus.Dead, now.AddMinutes(-1));
            var table = new InMemoryMembershipTable(
                new TableVersion(123, "123"),
                oldestDefunctEntry,
                removedDefunctEntry,
                retainedDefunctEntry);
            var cleanupAgent = this.CreateCleanupAgent(options, table, membershipManager);
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            ((ILifecycleParticipant<ISiloLifecycle>)cleanupAgent).Participate(lifecycle);

            await lifecycle.OnStart();
            membershipManager.Publish(Snapshot(
                Entry(this.localSilo, SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:200@200"), SiloStatus.Active, now),
                oldestDefunctEntry,
                removedDefunctEntry,
                retainedDefunctEntry));
            await Until(() => table.Calls.Any(call => call.Method == nameof(IMembershipTable.CleanupDefunctSiloEntries)));
            Assert.DoesNotContain(table.Calls, call => call.Method == nameof(IMembershipTable.ReadAll));

            var updatedTable = await table.ReadAll();
            Assert.Single(updatedTable.Members, member => member.Item1.Status == SiloStatus.Dead);
            Assert.Contains(updatedTable.Members, member => member.Item1.SiloAddress.Equals(retainedDefunctSilo));

            await lifecycle.OnStop();
        }

        [Fact]
        public async Task MembershipTableCleanupAgent_ThresholdCleanup_RetainsRecentlySuspectedEntries()
        {
            var now = this.timeProvider.GetUtcNow();
            var recentlySuspectedSilo = Silo("127.0.0.1:700@100");
            var options = new ClusterMembershipOptions { DefunctSiloCleanupPeriod = null, MaxDefunctSiloEntries = 1 };
            var membershipManager = new TestMembershipManager();

            // This entry reported itself alive most recently, but was not suspected recently.
            var newerAliveEntry = Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead, now.AddMinutes(-2));

            // This entry last reported itself alive long ago, but was declared dead (suspected) very recently.
            // It is therefore the most-recently-updated defunct entry and should be retained even though its
            // IAmAliveTime is the oldest.
            var recentlySuspectedEntry = Entry(recentlySuspectedSilo, SiloStatus.Dead, now.AddMinutes(-10));
            recentlySuspectedEntry.AddSuspector(this.localSilo, now.AddMinutes(-1).UtcDateTime);

            var table = new InMemoryMembershipTable(
                new TableVersion(123, "123"),
                newerAliveEntry,
                recentlySuspectedEntry);
            var cleanupAgent = this.CreateCleanupAgent(options, table, membershipManager);
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            ((ILifecycleParticipant<ISiloLifecycle>)cleanupAgent).Participate(lifecycle);

            await lifecycle.OnStart();
            membershipManager.Publish(Snapshot(
                Entry(this.localSilo, SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:200@200"), SiloStatus.Active, now),
                newerAliveEntry,
                recentlySuspectedEntry));
            await Until(() => table.Calls.Any(call => call.Method == nameof(IMembershipTable.CleanupDefunctSiloEntries)));

            var updatedTable = await table.ReadAll();
            Assert.Single(updatedTable.Members, member => member.Item1.Status == SiloStatus.Dead);
            Assert.Contains(updatedTable.Members, member => member.Item1.SiloAddress.Equals(recentlySuspectedSilo));

            await lifecycle.OnStop();
        }

        [Fact]
        public async Task MembershipTableCleanupAgent_ExpirationCleanup_SkipsUntilExpiredNonActiveEntryOrCleanupPeriodElapsed()
        {
            var options = new ClusterMembershipOptions
            {
                DefunctSiloCleanupPeriod = TimeSpan.FromMinutes(90),
                DefunctSiloExpiration = TimeSpan.FromDays(1),
                MaxDefunctSiloEntries = null
            };
            var membershipManager = new TestMembershipManager();
            var table = new InMemoryMembershipTable();
            var cleanupAgent = this.CreateCleanupAgent(options, table, membershipManager);
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            ((ILifecycleParticipant<ISiloLifecycle>)cleanupAgent).Participate(lifecycle);

            await lifecycle.OnStart();
            var now = this.timeProvider.GetUtcNow();
            membershipManager.Publish(Snapshot(Entry(this.localSilo, SiloStatus.Active, now)));
            await Until(() => CleanupCallCount(table) == 1);

            table.ClearCalls();
            membershipManager.Publish(Snapshot(Entry(this.localSilo, SiloStatus.Active, now)));
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.Equal(0, CleanupCallCount(table));

            var expiredNonActiveEntry = Entry(
                Silo("127.0.0.1:500@100"),
                SiloStatus.Joining,
                now - options.DefunctSiloExpiration - TimeSpan.FromTicks(1));
            membershipManager.Publish(Snapshot(
                Entry(this.localSilo, SiloStatus.Active, now),
                expiredNonActiveEntry));
            await Until(() => CleanupCallCount(table) == 1);

            table.ClearCalls();
            this.timeProvider.Advance(options.DefunctSiloCleanupPeriod.Value);
            var later = this.timeProvider.GetUtcNow();
            membershipManager.Publish(Snapshot(Entry(this.localSilo, SiloStatus.Active, later)));
            await Until(() => CleanupCallCount(table) == 1);

            await lifecycle.OnStop();
        }

        private async Task BasicScenario(bool enabled)
        {
            var options = new ClusterMembershipOptions
            {
                DefunctSiloCleanupPeriod = enabled ? TimeSpan.FromMinutes(90) : null,
                DefunctSiloExpiration = TimeSpan.FromDays(1),
                MaxDefunctSiloEntries = null
            };
            var membershipManager = new TestMembershipManager();
            var cleanupCalled = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
            var now = this.timeProvider.GetUtcNow();
            var table = new InMemoryMembershipTable
            {
                OnCleanupDefunctSiloEntries = beforeDate => cleanupCalled.TrySetResult(beforeDate)
            };
            var cleanupAgent = this.CreateCleanupAgent(options, table, membershipManager);
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            ((ILifecycleParticipant<ISiloLifecycle>)cleanupAgent).Participate(lifecycle);

            await lifecycle.OnStart();
            Assert.DoesNotContain(table.Calls, c => c.Method.Equals(nameof(IMembershipTable.CleanupDefunctSiloEntries)));

            membershipManager.Publish(Snapshot(Entry(this.localSilo, SiloStatus.Active, now)));
            if (enabled)
            {
                await Until(() => cleanupCalled.Task.IsCompleted);
                Assert.Equal(now - options.DefunctSiloExpiration, cleanupCalled.Task.Result);
                Assert.Contains(table.Calls, c => c.Method.Equals(nameof(IMembershipTable.CleanupDefunctSiloEntries)));
            }
            else
            {
                var completed = await Task.WhenAny(cleanupCalled.Task, Task.Delay(TimeSpan.FromMilliseconds(200)));
                Assert.NotSame(cleanupCalled.Task, completed);
                Assert.DoesNotContain(table.Calls, c => c.Method.Equals(nameof(IMembershipTable.CleanupDefunctSiloEntries)));
            }

            await lifecycle.OnStop();
        }

        private MembershipTableCleanupAgent CreateCleanupAgent(
            ClusterMembershipOptions options,
            InMemoryMembershipTable table,
            IMembershipManager membershipManager)
        {
            return new MembershipTableCleanupAgent(
                Options.Create(options),
                table,
                membershipManager,
                this.localSiloDetails,
                this.timeProvider,
                this.loggerFactory.CreateLogger<MembershipTableCleanupAgent>());
        }

        private static async Task Until(Func<bool> condition)
        {
            var maxTimeout = 40_000;
            while (!condition() && (maxTimeout -= 10) > 0) await Task.Delay(10);
            Assert.True(maxTimeout > 0);
        }

        private static int CleanupCallCount(InMemoryMembershipTable table) => table.Calls.Count(call => call.Method == nameof(IMembershipTable.CleanupDefunctSiloEntries));

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status, DateTimeOffset iAmAliveTime)
        {
            return new MembershipEntry { SiloAddress = address, Status = status, IAmAliveTime = iAmAliveTime.UtcDateTime, StartTime = iAmAliveTime.UtcDateTime };
        }

        private static MembershipTableSnapshot Snapshot(params MembershipEntry[] entries)
        {
            var entryList = entries.Select(e => Tuple.Create(e, "test")).ToList();
            return MembershipTableSnapshot.Create(new MembershipTableData(entryList, new TableVersion(12, "test")));
        }

        private sealed class TestMembershipManager : IMembershipManager
        {
            private readonly Channel<MembershipTableSnapshot> updates = Channel.CreateUnbounded<MembershipTableSnapshot>();

            public MembershipTableSnapshot CurrentSnapshot { get; private set; } = Snapshot();
            public IAsyncEnumerable<MembershipTableSnapshot> MembershipUpdates => this.updates.Reader.ReadAllAsync();
            public SiloStatus LocalSiloStatus => SiloStatus.Active;

            public void Publish(MembershipTableSnapshot snapshot)
            {
                this.CurrentSnapshot = snapshot;
                Assert.True(this.updates.Writer.TryWrite(snapshot));
            }

            public Task UpdateLocalStatus(SiloStatus status, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<bool> TryKillSilo(SiloAddress silo, CancellationToken cancellationToken) => Task.FromResult(false);
            public Task<bool> TrySuspectSilo(SiloAddress silo, SiloAddress indirectProbingSilo, CancellationToken cancellationToken) => Task.FromResult(false);
            public Task Refresh(MembershipVersion? targetVersion, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task ProcessGossipSnapshot(MembershipTableSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task UpdateIAmAlive(CancellationToken cancellationToken) => Task.CompletedTask;
            public void Participate(ISiloLifecycle lifecycle) { }

            public bool CheckHealth(DateTime lastCheckTime, out string reason)
            {
                reason = default;
                return true;
            }
        }
    }
}
