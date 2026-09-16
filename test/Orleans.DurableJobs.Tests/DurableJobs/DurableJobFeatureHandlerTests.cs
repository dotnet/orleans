using Orleans;
using Orleans.DurableJobs;
using Xunit;

namespace Tester.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
public class DurableJobFeatureHandlerTests
{
    [Fact]
    public void Registry_SelectsHandlerUsingHandlerOwnedMatching()
    {
        var registry = new DurableJobHandlerRegistry();
        var lower = new TestHandler(static jobName => jobName.StartsWith("feature.", StringComparison.Ordinal));
        var upper = new TestHandler(static jobName => jobName.StartsWith("Feature.", StringComparison.Ordinal));

        registry.Register(lower);
        registry.Register(upper);

        Assert.True(registry.TryGetHandler("feature.cleanup", out var resolvedLower));
        Assert.True(registry.TryGetHandler("Feature.cleanup", out var resolvedUpper));
        Assert.False(registry.TryGetHandler("other.cleanup", out var unresolved));
        Assert.Same(lower, resolvedLower);
        Assert.Same(upper, resolvedUpper);
        Assert.Null(unresolved);
    }

    [Fact]
    public void Registry_RejectsDuplicateHandlerRegistration()
    {
        var registry = new DurableJobHandlerRegistry();
        var handler = new TestHandler(static _ => true);

        registry.Register(handler);

        Assert.Throws<InvalidOperationException>(() => registry.Register(handler));
    }

    [Fact]
    public void Registry_RejectsAmbiguousMatches()
    {
        var registry = new DurableJobHandlerRegistry();
        registry.Register(new TestHandler(static jobName => jobName.StartsWith("feature.", StringComparison.Ordinal)));
        registry.Register(new TestHandler(static jobName => jobName.EndsWith(".cleanup", StringComparison.Ordinal)));

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.TryGetHandler("feature.cleanup", out _));

        Assert.Contains("Multiple durable job feature handlers match job 'feature.cleanup'", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TestHandler(Func<string, bool> canHandle) : IDurableJobFeatureHandler
    {
        public bool CanHandle(string jobName) => canHandle(jobName);

        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(DurableJobRunResult.Completed);
    }
}
