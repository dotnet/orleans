#nullable enable
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Tester.Directories;
using TestExtensions;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
[TestCategory("BVT"), TestCategory("Directory")]
public sealed class DefaultGrainDirectoryTests(DefaultClusterFixture fixture, ITestOutputHelper output)
    : GrainDirectoryTests<IGrainDirectory>(output), IClassFixture<DefaultClusterFixture>
{
    private readonly TestCluster _testCluster = fixture.HostedCluster;
    private InProcessSiloHandle Primary => (InProcessSiloHandle)_testCluster.Primary!;

    protected override IGrainDirectory CreateGrainDirectory() =>
        Primary.SiloHost.Services.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
[TestCategory("BVT"), TestCategory("Directory")]
public sealed class DistributedGrainDirectoryMembershipTests
{
    [Fact]
    public async Task OwnerResolutionWaitsForActiveDirectoryMembership()
    {
        var builder = new InProcessTestClusterBuilder(1);
#pragma warning disable ORLEANSEXP003
        builder.Options.UseDistributedGrainDirectory = true;
#pragma warning restore ORLEANSEXP003
        builder.ConfigureSilo((_, siloBuilder) => siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<ControlledMembershipService>();
            services.Replace(ServiceDescriptor.Singleton(static sp => new DirectoryMembershipService(
                sp.GetRequiredService<ControlledMembershipService>(),
                sp.GetRequiredService<IInternalGrainFactory>(),
                sp.GetRequiredService<ILogger<DirectoryMembershipService>>(),
                sp.GetRequiredService<IOptions<GrainDirectoryOptions>>().Value.PartitionsPerSilo,
                DirectoryMembershipSnapshot.DefaultGetRingBoundaries)));
        }));
        var cluster = builder.Build();

        try
        {
            await cluster.DeployAsync();
            var silo = cluster.Silos[0];
            var membership = silo.ServiceProvider.GetRequiredService<DirectoryMembershipService>();
            var directory = silo.ServiceProvider.GetRequiredService<DistributedGrainDirectory>();
            var testHooks = (DistributedGrainDirectory.ITestHooks)directory;
            var controlledMembership = silo.ServiceProvider.GetRequiredService<ControlledMembershipService>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await controlledMembership.ActiveUpdateBlocked.WaitAsync(timeout.Token);
            var siloAddress = silo.SiloAddress;
            Assert.Empty(membership.CurrentView.Members);

            var grainId = GrainId.Create("directory-readiness", "registration");
            var primaryTask = testHooks.WaitForPrimaryForGrain(grainId, timeout.Token);
            Assert.False(primaryTask.IsCompleted);

            controlledMembership.ReleaseActiveUpdate();

            Assert.Equal(siloAddress, await primaryTask);
        }
        finally
        {
            await cluster.DisposeAsync();
        }
    }

    private sealed class ControlledMembershipService(IClusterMembershipService inner) : IClusterMembershipService
    {
        private readonly TaskCompletionSource _activeUpdateBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseActiveUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hasBlockedActiveUpdate;

        public ClusterMembershipSnapshot CurrentSnapshot => inner.CurrentSnapshot;

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => GetMembershipUpdates();

        public Task ActiveUpdateBlocked => _activeUpdateBlocked.Task;

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) =>
            inner.Refresh(minimumVersion, cancellationToken);

        public Task<bool> TryKill(SiloAddress siloAddress) => inner.TryKill(siloAddress);

        public void ReleaseActiveUpdate() => _releaseActiveUpdate.TrySetResult();

        private async IAsyncEnumerable<ClusterMembershipSnapshot> GetMembershipUpdates(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in inner.MembershipUpdates.WithCancellation(cancellationToken))
            {
                if (update.Members.Values.Any(static member => member.Status == SiloStatus.Active)
                    && Interlocked.CompareExchange(ref _hasBlockedActiveUpdate, 1, 0) == 0)
                {
                    var joiningVersion = new MembershipVersion(update.Version.Value - 1);
                    if (joiningVersion <= MembershipVersion.MinValue)
                    {
                        throw new InvalidOperationException($"Expected an Active membership version greater than one, encountered {update.Version}.");
                    }

                    var joiningMembers = update.Members.ToImmutableDictionary(
                        static member => member.Key,
                        static member => new ClusterMember(member.Key, SiloStatus.Joining, member.Value.Name));
                    yield return new ClusterMembershipSnapshot(joiningMembers, joiningVersion);
                    _activeUpdateBlocked.TrySetResult();
                    await _releaseActiveUpdate.Task.WaitAsync(cancellationToken);
                }

                yield return update;
            }
        }
    }
}
