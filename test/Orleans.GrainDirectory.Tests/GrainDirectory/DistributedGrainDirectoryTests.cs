#nullable enable
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Tester.Directories;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

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
        var cluster = builder.Build();

        try
        {
            await cluster.DeployAsync();
            var silo = cluster.Silos[0];
            var membership = silo.ServiceProvider.GetRequiredService<DirectoryMembershipService>();
            var directory = silo.ServiceProvider.GetRequiredService<DistributedGrainDirectory>();
            var testHooks = (DistributedGrainDirectory.ITestHooks)directory;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var activeView = membership.CurrentView;
            if (activeView.Members.Length == 0)
            {
                await foreach (var view in membership.ViewUpdates.WithCancellation(timeout.Token))
                {
                    if (view.Members.Length > 0)
                    {
                        activeView = view;
                        break;
                    }
                }
            }

            var siloAddress = silo.SiloAddress;
            var activeMember = activeView.ClusterMembershipSnapshot.Members[siloAddress];
            var joiningVersion = activeView.Version;
            membership.PublishMembershipUpdate(CreateSnapshot(siloAddress, activeMember.Name, SiloStatus.Joining, joiningVersion));
            Assert.Empty(membership.CurrentView.Members);

            var grainId = GrainId.Create("directory-readiness", "registration");
            var primaryTask = testHooks.WaitForPrimaryForGrain(grainId, timeout.Token);
            Assert.False(primaryTask.IsCompleted);

            membership.PublishMembershipUpdate(CreateSnapshot(
                siloAddress,
                activeMember.Name,
                SiloStatus.Active,
                new MembershipVersion(joiningVersion.Value + 1)));

            Assert.Equal(siloAddress, await primaryTask);
        }
        finally
        {
            await cluster.DisposeAsync();
        }
    }

    private static ClusterMembershipSnapshot CreateSnapshot(
        SiloAddress siloAddress,
        string siloName,
        SiloStatus status,
        MembershipVersion version) =>
        new(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(
                siloAddress,
                new ClusterMember(siloAddress, status, siloName)),
            version);
}
