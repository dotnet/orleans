using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.Management;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT"), TestCategory("Management")]
public sealed class ManagementGrainActivationCountTests
{
    [Fact]
    public async Task GetTotalActivationCount_SumsCountsFromEveryActiveSilo()
    {
        var (managementGrain, controls) = CreateManagementGrain();
        controls[0].GetActivationCount(Arg.Any<CancellationToken>()).Returns(Task.FromResult(17));
        controls[1].GetActivationCount(Arg.Any<CancellationToken>()).Returns(Task.FromResult(25));

        var result = await managementGrain.GetTotalActivationCount(TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        foreach (var control in controls)
        {
            _ = control.Received(1).GetActivationCount(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task GetTotalActivationCount_PropagatesSiloFailure()
    {
        var (managementGrain, controls) = CreateManagementGrain();
        var failure = new InvalidOperationException("Silo request failed.");
        controls[0].GetActivationCount(Arg.Any<CancellationToken>()).Returns(Task.FromResult(17));
        _ = controls[1].GetActivationCount(Arg.Any<CancellationToken>()).Returns(Task.FromException<int>(failure));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => managementGrain.GetTotalActivationCount(TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task GetTotalActivationCount_PropagatesSiloCancellation()
    {
        var (managementGrain, controls) = CreateManagementGrain();
        using var siloCancellation = new CancellationTokenSource();
        siloCancellation.Cancel();
        controls[0].GetActivationCount(Arg.Any<CancellationToken>()).Returns(Task.FromResult(17));
        controls[1].GetActivationCount(Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<int>(siloCancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => managementGrain.GetTotalActivationCount(TestContext.Current.CancellationToken));
    }

    private static (ManagementGrain ManagementGrain, ISiloControl[] Controls) CreateManagementGrain()
    {
        var firstSilo = SiloAddress.FromParsableString("127.0.0.1:11111@1");
        var secondSilo = SiloAddress.FromParsableString("127.0.0.1:22222@1");
        var hosts = new Dictionary<SiloAddress, SiloStatus>
        {
            [firstSilo] = SiloStatus.Active,
            [secondSilo] = SiloStatus.Active
        };
        var statusOracle = Substitute.For<ISiloStatusOracle>();
        statusOracle.GetApproximateSiloStatuses(onlyActive: true).Returns(hosts);
        var membershipManager = Substitute.For<IMembershipManager>();
        membershipManager.Refresh(null, Arg.Any<CancellationToken>(), false).Returns(Task.CompletedTask);

        var firstControl = Substitute.For<ISiloControl>();
        var secondControl = Substitute.For<ISiloControl>();
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, firstSilo).Returns(firstControl);
        grainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, secondSilo).Returns(secondControl);

        var clusterManifestProvider = Substitute.For<IClusterManifestProvider>();
        var managementGrain = new ManagementGrain(
            grainFactory,
            statusOracle,
            versionStore: null!,
            logger: null!,
            membershipManager,
            clusterManifestProvider,
            catalog: null!,
            grainLocator: null!);

        return (managementGrain, [firstControl, secondControl]);
    }
}
