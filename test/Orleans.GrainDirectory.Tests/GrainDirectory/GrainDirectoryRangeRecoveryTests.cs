using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
[TestCategory("BVT"), TestCategory("Directory")]
public sealed class GrainDirectoryRangeRecoveryTests
{
    [Fact]
    public async Task RecoveryEnumeratesExactRangesAndPreservesPublishedActivations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new InProcessTestClusterBuilder(1);
#pragma warning disable ORLEANSEXP003
        builder.Options.UseDistributedGrainDirectory = true;
#pragma warning restore ORLEANSEXP003
        await using var cluster = builder.Build();
        await cluster.DeployAsync(cancellationToken);

        var grains = Enumerable.Range(0, 64)
            .Select(index => cluster.Client.GetGrain<IMyDirectoryTestGrain>(index))
            .ToArray();
        await Task.WhenAll(grains.Select(grain => grain.Ping().AsTask()));

        var services = cluster.Silos[0].ServiceProvider;
        var activations = services.GetRequiredService<ActivationDirectory>();
        var directory = services.GetRequiredService<DistributedGrainDirectory>();
        var membership = services.GetRequiredService<DirectoryMembershipService>();
        var contexts = grains.Select(grain => activations.FindTarget(grain.GetGrainId())!).ToArray();
        Assert.All(contexts, static context => Assert.NotNull(context));
        var addresses = contexts.Select(static context => context.Address).ToArray();
        var ids = addresses.Select(static address => address.GrainId).ToHashSet();
        var hashes = ids.Select(static id => id.GetUniformHashCode()).Order().ToArray();
        var range = RingRange.Create(hashes[15], hashes[47]);
        var recoveryVersion = new MembershipVersion(Math.Max(
            membership.CurrentView.Version.Value,
            directory.RecoveryMembershipVersion + 1));

        foreach (var query in new[] { range, range.Complement(), RingRange.FromPoint(hashes[0]), RingRange.Empty, RingRange.Full })
        {
            var result = await directory.GetRegisteredActivations(recoveryVersion, query, isValidation: false, cancellationToken);
            Assert.All(result.Value, address => Assert.True(query.Contains(address.GrainId)));
            var expected = addresses.Where(address => query.Contains(address.GrainId)).OrderBy(static address => address.GrainId).ToArray();
            var actual = result.Value.Where(address => ids.Contains(address.GrainId)).OrderBy(static address => address.GrainId).ToArray();

            Assert.Equal(expected.Length, actual.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Same(expected[i], actual[i]);
                Assert.Equal(expected[i].MembershipVersion, actual[i].MembershipVersion);
            }

            Assert.Equal(recoveryVersion.Value, directory.RecoveryMembershipVersion);
            Assert.All(contexts, context => Assert.Same(context, activations.FindTarget(context.GrainId)));
        }

        await directory.GetRegisteredActivations(new MembershipVersion(recoveryVersion.Value - 1), range, isValidation: false, cancellationToken);
        Assert.Equal(recoveryVersion.Value, directory.RecoveryMembershipVersion);
    }
}
