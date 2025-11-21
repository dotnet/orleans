using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests;

/// <summary>
/// Tests for ILocalActivationStatusChecker functionality.
/// Validates that the activation status checker correctly identifies locally activated grains
/// from both silo and client perspectives.
/// </summary>
public class LocalActivationStatusCheckerTests(DefaultClusterFixture fixture) : HostedTestClusterEnsureDefaultStarted(fixture)
{
    /// <summary>
    /// Tests that IsLocallyActivated returns true for a grain that has been activated on the local silo.
    /// Verifies that after calling a grain method (which activates the grain), the silo-side
    /// ILocalActivationStatusChecker correctly identifies the grain as locally activated.
    /// </summary>
    [Fact, TestCategory("BVT")]
    public async Task ShouldReturnTrueForLocallyActivatedGrain()
    {
        // Arrange: Get a grain reference and activate it by calling a method
        var grain = GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.NextInt64());
        await grain.SetA(42);

        // Act & Assert: The grain should be locally activated on exactly one silo
        var grainId = grain.GetGrainId();
        Assert.Single(HostedCluster.Silos, silo => IsLocallyActivated(silo, grainId));

        static bool IsLocallyActivated(SiloHandle silo, GrainId grainId)
        {
            var siloHandle = (InProcessSiloHandle)silo;
            var checker = siloHandle.ServiceProvider.GetRequiredService<ILocalActivationStatusChecker>();
            return checker.IsLocallyActivated(grainId);
        }
    }

    /// <summary>
    /// Tests that IsLocallyActivated returns false for a grain that has not been activated.
    /// Verifies that a grain reference that has been created but never invoked is not
    /// considered locally activated.
    /// </summary>
    [Fact, TestCategory("BVT")]
    public async Task ShouldReturnFalseForNonActivatedGrain()
    {
        // Arrange: Get a grain reference but don't activate it
        var grain = GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.NextInt64());

        // Act & Assert: The grain should not be locally activated on any silo
        var grainId = grain.GetGrainId();
        Assert.DoesNotContain(HostedCluster.Silos, silo => IsLocallyActivated(silo, grainId));

        // Clean up by ensuring we don't leave state behind
        await Task.CompletedTask;

        static bool IsLocallyActivated(SiloHandle silo, GrainId grainId)
        {
            var siloHandle = (InProcessSiloHandle)silo;
            var checker = siloHandle.ServiceProvider.GetRequiredService<ILocalActivationStatusChecker>();
            return checker.IsLocallyActivated(grainId);
        }
    }

    /// <summary>
    /// Tests that IsLocallyActivated returns false for different grain identities.
    /// Verifies that activating one grain does not affect the activation status of another grain.
    /// </summary>
    [Fact, TestCategory("BVT")]
    public async Task ShouldReturnFalseForDifferentGrainIdentity()
    {
        // Arrange: Activate one grain
        var grain1 = GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.NextInt64());
        await grain1.SetA(42);

        // Get a different grain reference (different identity)
        var grain2 = GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.NextInt64());

        // Act & Assert: The second grain should not be locally activated on any silo
        var grainId2 = grain2.GetGrainId();
        Assert.DoesNotContain(HostedCluster.Silos, silo => IsLocallyActivated(silo, grainId2));

        static bool IsLocallyActivated(SiloHandle silo, GrainId grainId)
        {
            var siloHandle = (InProcessSiloHandle)silo;
            var checker = siloHandle.ServiceProvider.GetRequiredService<ILocalActivationStatusChecker>();
            return checker.IsLocallyActivated(grainId);
        }
    }

    /// <summary>
    /// Tests that the client-side ILocalActivationStatusChecker always returns false.
    /// Clients do not host grain activations, so IsLocallyActivated should always return false
    /// regardless of whether a grain has been invoked or not.
    /// </summary>
    [Fact, TestCategory("BVT")]
    public async Task ClientShouldAlwaysReturnFalseForIsLocallyActivated()
    {
        // Arrange: Get a grain reference and activate it
        var grain = GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.NextInt64());
        await grain.SetA(42);

        // Act: Get the client-side activation checker
        var clientChecker = Client.ServiceProvider.GetRequiredService<ILocalActivationStatusChecker>();
        var isLocalOnClient = clientChecker.IsLocallyActivated(grain.GetGrainId());

        // Assert: Client should always return false
        Assert.False(isLocalOnClient, "Client should always return false for IsLocallyActivated");
    }

    /// <summary>
    /// Tests that the client-side checker returns false even for non-activated grains.
    /// Verifies consistent behavior regardless of grain activation state.
    /// </summary>
    [Fact, TestCategory("BVT")]
    public void ClientShouldReturnFalseForNonActivatedGrain()
    {
        // Arrange: Get a grain reference without activating it
        var grain = GrainFactory.GetGrain<ISimpleGrain>(Random.Shared.NextInt64());

        // Act: Get the client-side activation checker
        var clientChecker = Client.ServiceProvider.GetRequiredService<ILocalActivationStatusChecker>();
        var isLocalOnClient = clientChecker.IsLocallyActivated(grain.GetGrainId());

        // Assert: Client should return false
        Assert.False(isLocalOnClient, "Client should return false for non-activated grain");
    }
}
