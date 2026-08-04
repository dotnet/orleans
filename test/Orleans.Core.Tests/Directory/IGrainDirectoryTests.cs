using Orleans.GrainDirectory;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.Directory;

[TestCategory("BVT"), TestCategory("Directory")]
public class IGrainDirectoryTests
{
    [Fact]
    public async Task CancellationOverloads_DefaultToCancelableWait()
    {
        IGrainDirectory directory = new BlockingGrainDirectory();
        var cancellationToken = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => directory.Register(default!, cancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => directory.Register(default!, null, cancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => directory.Unregister(default!, cancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => directory.Lookup(default, cancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => directory.UnregisterSilos([], cancellationToken));
    }

    private sealed class BlockingGrainDirectory : IGrainDirectory
    {
        public Task<GrainAddress?> Register(GrainAddress address) =>
            new TaskCompletionSource<GrainAddress?>().Task;

        public Task Unregister(GrainAddress address) =>
            new TaskCompletionSource().Task;

        public Task<GrainAddress?> Lookup(GrainId grainId) =>
            new TaskCompletionSource<GrainAddress?>().Task;

        public Task UnregisterSilos(List<SiloAddress> siloAddresses) =>
            new TaskCompletionSource().Task;
    }
}
