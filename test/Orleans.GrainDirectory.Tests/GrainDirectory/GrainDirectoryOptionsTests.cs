#nullable enable
using Documentation.Deployment;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Directory")]
public sealed class GrainDirectoryOptionsTests
{
    [Fact]
    public async Task PartitionsPerSilo_IsConfigurable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, siloBuilder) =>
        {
#pragma warning disable ORLEANSEXP003
            siloBuilder.Configure<GrainDirectoryOptions>(options => options.PartitionsPerSilo = 3);
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
        });

        var cluster = builder.Build();
        try
        {
            await cluster.DeployAsync(cancellationToken);
            var membershipService = cluster.Silos[0].ServiceProvider.GetRequiredService<DirectoryMembershipService>();

            Assert.Equal(3, membershipService.PartitionsPerSilo);
        }
        finally
        {
            await cluster.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPartitionBoundaries_IsConfigurable(bool useLegacyMapping)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            if (useLegacyMapping)
            {
                DirectoryPartitioningSnippet.Configure(siloBuilder);
            }
            else
            {
                siloBuilder.Configure<GrainDirectoryOptions>(options =>
                {
                    options.PartitionsPerSilo = 3;
                    options.GetPartitionBoundaries = static (_, _) => [300, 100, 200];
                });
            }
#pragma warning disable ORLEANSEXP003
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
        });

        var cluster = builder.Build();
        try
        {
            await cluster.DeployAsync(cancellationToken);
            var silo = cluster.Silos[0];
            var membershipService = silo.ServiceProvider.GetRequiredService<DirectoryMembershipService>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var snapshot = await membershipService.RefreshViewAsync(
                membershipService.ClusterMembershipService.CurrentSnapshot.Version, timeout.Token);
            var expectedBoundaries = useLegacyMapping
                ? silo.SiloAddress.GetUniformHashCodes(30).Order().ToArray()
                : [300u, 100u, 200u];
            var sortedBoundaries = expectedBoundaries.Order().ToArray();

            Assert.Equal(silo.SiloAddress, Assert.Single(snapshot.Members));
            Assert.Equal(expectedBoundaries.Length, membershipService.PartitionsPerSilo);
            for (var partitionIndex = 0; partitionIndex < expectedBoundaries.Length; partitionIndex++)
            {
                var start = expectedBoundaries[partitionIndex];
                var sortedIndex = Array.IndexOf(sortedBoundaries, start);
                var end = sortedBoundaries[(sortedIndex + 1) % sortedBoundaries.Length];
                Assert.Equal(RingRange.Create(start, end), snapshot.GetRange(silo.SiloAddress, partitionIndex));
                Assert.True(snapshot.TryGetOwner(end, out var owner, out var partition));
                Assert.Equal(silo.SiloAddress, owner);
                Assert.Equal(GrainDirectoryPartition.CreateGrainId(silo.SiloAddress, partitionIndex).GrainId, partition.GetGrainId());
            }
        }
        finally
        {
            await cluster.DisposeAsync();
        }
    }
}
