using System.Net;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public sealed class ClusterManifestStabilizationHelperTests
{
    [Fact]
    public async Task WaitForExpectedClusterManifestAsync_WhenActiveSilosAreNull_ThrowsArgumentNullException()
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => ClusterManifestStabilizationHelper.WaitForExpectedClusterManifestAsync(
                activeSilos: null!,
                testHooks: null!,
                timeout: TimeSpan.FromSeconds(17)));

        Assert.Equal("activeSilos", exception.ParamName);
    }

    [Fact]
    public async Task WaitForExpectedClusterManifestAsync_WhenHooksAreNull_ThrowsArgumentNullException()
    {
        using var silo = CreateSilo(port: 11111, generation: 7);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => ClusterManifestStabilizationHelper.WaitForExpectedClusterManifestAsync(
                activeSilos: [silo],
                testHooks: null!,
                timeout: TimeSpan.FromSeconds(17)));

        Assert.Equal("testHooks", exception.ParamName);
    }

    [Fact]
    public async Task WaitForExpectedClusterManifestAsync_WaitsForHookFinalResult()
    {
        using var silo = CreateSilo(port: 11111, generation: 7);
        TimeSpan? observedTimeout = null;

        var result = await ClusterManifestStabilizationHelper.WaitForExpectedClusterManifestAsync(
            activeSilos: [silo],
            waitForClusterManifest: [WaitForClusterManifest],
            timeout: TimeSpan.Zero);

        Assert.True(result);
        Assert.Equal(TimeSpan.Zero, observedTimeout);

        async Task<bool> WaitForClusterManifest(SiloAddress[] expectedSilos, TimeSpan timeout)
        {
            observedTimeout = timeout;
            await Task.Yield();
            return expectedSilos.SequenceEqual([silo.SiloAddress]);
        }
    }

    private static TestSiloHandle CreateSilo(int port, int generation) =>
        new()
        {
            Name = $"Silo-{generation}",
            SiloAddress = SiloAddress.New(IPAddress.Loopback, port, generation),
            GatewayAddress = SiloAddress.New(IPAddress.Loopback, port + 1, generation),
        };

    private sealed class TestSiloHandle : SiloHandle
    {
        public override bool IsActive => false;

        public override Task StopSiloAsync(bool stopGracefully) => throw new NotSupportedException();

        public override Task StopSiloAsync(CancellationToken ct) => throw new NotSupportedException();

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
