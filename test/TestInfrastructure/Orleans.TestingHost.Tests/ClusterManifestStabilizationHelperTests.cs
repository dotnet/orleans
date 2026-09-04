using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Orleans.Metadata;
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
    private static readonly GrainManifest EmptyGrainManifest = new(
        ImmutableDictionary<GrainType, GrainProperties>.Empty,
        ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

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

    [Fact]
    public async Task WaitForExpectedClusterManifestAsync_WaitsForManifestUpdate()
    {
        using var firstSilo = CreateSilo(port: 11111, generation: 7);
        using var secondSilo = CreateSilo(port: 11112, generation: 8);
        var provider = new TestClusterManifestProvider(CreateManifest(firstSilo.SiloAddress));

        var wait = ClusterManifestStabilizationHelper.WaitForExpectedClusterManifestAsync(
            activeSilos: [firstSilo, secondSilo],
            manifestProviders:
            [
                provider,
                new TestClusterManifestProvider(CreateManifest(firstSilo.SiloAddress, secondSilo.SiloAddress)),
            ],
            TestContext.Current.CancellationToken);

        Assert.False(wait.IsCompleted);

        provider.Update(CreateManifest(firstSilo.SiloAddress, secondSilo.SiloAddress));
        await wait;
    }

    private static ClusterManifest CreateManifest(params SiloAddress[] silos)
        => new(
            MajorMinorVersion.Zero,
            silos.ToImmutableDictionary(static silo => silo, _ => EmptyGrainManifest));

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

    private sealed class TestClusterManifestProvider : IClusterManifestProvider
    {
        private readonly Channel<ClusterManifest> _updates = Channel.CreateUnbounded<ClusterManifest>();

        public TestClusterManifestProvider(ClusterManifest current)
        {
            Current = current;
            LocalGrainManifest = EmptyGrainManifest;
        }

        public ClusterManifest Current { get; private set; }

        public IAsyncEnumerable<ClusterManifest> Updates => GetUpdates();

        public GrainManifest LocalGrainManifest { get; }

        public void Update(ClusterManifest manifest)
        {
            Current = manifest;
            Assert.True(_updates.Writer.TryWrite(manifest));
        }

        private async IAsyncEnumerable<ClusterManifest> GetUpdates(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Current;
            await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
            }
        }
    }
}
