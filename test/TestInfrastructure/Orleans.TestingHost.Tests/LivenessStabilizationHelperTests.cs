using System.Net;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public sealed class LivenessStabilizationHelperTests
{
    [Fact]
    public async Task WaitForExpectedActiveSilosAsync_WhenActiveSilosAreNull_ThrowsArgumentNullException()
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => LivenessStabilizationHelper.WaitForExpectedActiveSilosAsync(
                activeSilos: null!,
                testHooks: null!,
                timeout: TimeSpan.FromSeconds(19)));

        Assert.Equal("activeSilos", exception.ParamName);
    }

    [Fact]
    public async Task WaitForExpectedActiveSilosAsync_WhenHooksAreNull_ThrowsArgumentNullException()
    {
        using var silo = CreateSilo(port: 31111, generation: 13);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => LivenessStabilizationHelper.WaitForExpectedActiveSilosAsync(
                activeSilos: [silo],
                testHooks: null!,
                timeout: TimeSpan.FromSeconds(19)));

        Assert.Equal("testHooks", exception.ParamName);
    }

    [Fact]
    public async Task WaitForExpectedActiveSilosAndGatewaysAsync_WhenGatewayManagerIsNull_ThrowsArgumentNullException()
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => LivenessStabilizationHelper.WaitForExpectedActiveSilosAndGatewaysAsync(
                activeSilos: null!,
                testHooks: null!,
                gatewayManager: null!,
                timeout: TimeSpan.FromSeconds(19)));

        Assert.Equal("gatewayManager", exception.ParamName);
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
