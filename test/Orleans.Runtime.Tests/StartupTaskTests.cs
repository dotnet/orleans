using System.Collections.Immutable;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;

using TestExtensions;

using UnitTests.GrainInterfaces;

using Xunit;

namespace DefaultCluster.Tests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("BVT"), TestCategory("Lifecycle")]
    public class StartupTaskTests : IClassFixture<StartupTaskTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
                builder.AddSiloBuilderConfigurator<StartupTaskSiloConfigurator>();
            }

            public bool ActiveDirectoryUpdateDelayed => GetPrimaryMembership().ActiveUpdateDelayed;

            public int DirectoryRefreshCount => GetPrimaryMembership().RefreshCount;

            public MembershipVersion StaleDirectoryVersion => GetPrimaryMembership().StaleDirectoryVersion;

            public MembershipVersion ActiveDirectoryVersion => GetPrimaryMembership().ActiveDirectoryVersion;

            public MembershipVersion FirstRefreshMinimumVersion => GetPrimaryMembership().FirstRefreshMinimumVersion;

            private DelayedDirectoryMembershipService GetPrimaryMembership()
            {
                var primary = HostedCluster.Primary as InProcessSiloHandle
                    ?? throw new InvalidOperationException("Expected an in-process primary silo.");
                return primary.SiloHost.Services.GetRequiredService<DelayedDirectoryMembershipService>();
            }

            private class StartupTaskSiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.ConfigureServices(services =>
                    {
                        services.AddSingleton<DelayedDirectoryMembershipService>();
                        services.Replace(ServiceDescriptor.Singleton(sp => new DirectoryMembershipService(
                            sp.GetRequiredService<DelayedDirectoryMembershipService>(),
                            sp.GetRequiredService<IInternalGrainFactory>(),
                            sp.GetRequiredService<ILogger<DirectoryMembershipService>>(),
                            sp.GetRequiredService<IOptions<GrainDirectoryOptions>>().Value.PartitionsPerSilo,
                            DirectoryMembershipSnapshot.DefaultGetRingBoundaries)));
                    });
                    hostBuilder.AddStartupTask<CallGrainStartupTask>();
                    hostBuilder.AddStartupTask(
                        async (services, cancellation) =>
                        {
                            var grainFactory = services.GetRequiredService<IGrainFactory>();
                            var grain = grainFactory.GetGrain<ISimpleGrain>(1);
                            await grain.SetA(888);
                        });
                }
            }

            private sealed class DelayedDirectoryMembershipService : IClusterMembershipService
            {
                private readonly IClusterMembershipService inner;
                private readonly TaskCompletionSource releaseActiveUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                private int activeUpdateDelayed;
                private int refreshCount;
                private long staleDirectoryVersion;
                private long activeDirectoryVersion;
                private long firstRefreshMinimumVersion;

                public DelayedDirectoryMembershipService(IClusterMembershipService inner)
                {
                    this.inner = inner;
                }

                public ClusterMembershipSnapshot CurrentSnapshot => this.inner.CurrentSnapshot;

                public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => this.GetMembershipUpdates();

                public bool ActiveUpdateDelayed => Volatile.Read(ref this.activeUpdateDelayed) != 0;

                public int RefreshCount => Volatile.Read(ref this.refreshCount);

                public MembershipVersion StaleDirectoryVersion => new(Volatile.Read(ref this.staleDirectoryVersion));

                public MembershipVersion ActiveDirectoryVersion => new(Volatile.Read(ref this.activeDirectoryVersion));

                public MembershipVersion FirstRefreshMinimumVersion => new(Volatile.Read(ref this.firstRefreshMinimumVersion));

                public async ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
                {
                    var staleVersion = Volatile.Read(ref this.staleDirectoryVersion);
                    if (Volatile.Read(ref this.activeUpdateDelayed) != 0 && minimumVersion.Value > staleVersion)
                    {
                        Interlocked.Increment(ref this.refreshCount);
                        Interlocked.CompareExchange(ref this.firstRefreshMinimumVersion, minimumVersion.Value, 0);
                        this.releaseActiveUpdate.TrySetResult();
                    }

                    await this.inner.Refresh(minimumVersion, cancellationToken);
                }

                public Task<bool> TryKill(SiloAddress siloAddress) => this.inner.TryKill(siloAddress);

                private async IAsyncEnumerable<ClusterMembershipSnapshot> GetMembershipUpdates(
                    [EnumeratorCancellation] CancellationToken cancellationToken = default)
                {
                    await foreach (var update in this.inner.MembershipUpdates.WithCancellation(cancellationToken))
                    {
                        if (update.Members.Values.Any(static member => member.Status == SiloStatus.Active)
                            && Interlocked.CompareExchange(ref this.activeUpdateDelayed, 1, 0) == 0)
                        {
                            var staleVersion = update.Version.Value - 1;
                            if (staleVersion <= 0)
                            {
                                throw new InvalidOperationException($"Expected an Active membership version greater than one, encountered {update.Version}.");
                            }

                            // Keep the directory on a stale Joining view until it explicitly refreshes to the Active view.
                            var joiningMembers = update.Members.ToImmutableDictionary(
                                static member => member.Key,
                                static member => new ClusterMember(member.Key, SiloStatus.Joining, member.Value.Name));
                            Volatile.Write(ref this.staleDirectoryVersion, staleVersion);
                            Volatile.Write(ref this.activeDirectoryVersion, update.Version.Value);
                            yield return new ClusterMembershipSnapshot(joiningMembers, new(staleVersion));
                            await this.releaseActiveUpdate.Task.WaitAsync(cancellationToken);
                        }

                        yield return update;
                    }
                }
            }
        }

        public class CallGrainStartupTask : IStartupTask
        {
            private readonly IGrainFactory grainFactory;

            public CallGrainStartupTask(IGrainFactory grainFactory)
            {
                this.grainFactory = grainFactory;
            }

            public async Task Execute(CancellationToken cancellationToken)
            {
                var grain = this.grainFactory.GetGrain<ISimpleGrain>(2);
                await grain.SetA(777);
            }
        }

        private readonly Fixture fixture;

        public StartupTaskTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        /// <summary>
        /// Ensures that startup tasks can call grains.
        /// </summary>
        [Fact]
        public async Task StartupTaskCanCallGrains()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ISimpleGrain>(1);
            var value = await grain.GetA();
            Assert.Equal(888, value);

            grain = this.fixture.GrainFactory.GetGrain<ISimpleGrain>(2);
            value = await grain.GetA();
            Assert.Equal(777, value);

            Assert.True(this.fixture.ActiveDirectoryUpdateDelayed);
            Assert.True(this.fixture.DirectoryRefreshCount > 0);
            Assert.True(this.fixture.StaleDirectoryVersion > MembershipVersion.MinValue);
            Assert.True(this.fixture.ActiveDirectoryVersion > this.fixture.StaleDirectoryVersion);
            Assert.Equal(this.fixture.ActiveDirectoryVersion, this.fixture.FirstRefreshMinimumVersion);
        }
    }
}
