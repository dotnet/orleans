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
        var lower = new TestHandler(static job => job.Name.StartsWith("feature.", StringComparison.Ordinal));
        var upper = new TestHandler(static job => job.Name.StartsWith("Feature.", StringComparison.Ordinal));

        registry.Register(lower);
        registry.Register(upper);

        Assert.True(registry.TryGetHandler(CreateJob("feature.cleanup"), out var resolvedLower));
        Assert.True(registry.TryGetHandler(CreateJob("Feature.cleanup"), out var resolvedUpper));
        Assert.False(registry.TryGetHandler(CreateJob("other.cleanup"), out var unresolved));
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
        registry.Register(new TestHandler(static job => job.Name.StartsWith("feature.", StringComparison.Ordinal)));
        registry.Register(new TestHandler(static job => job.Name.EndsWith(".cleanup", StringComparison.Ordinal)));

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.TryGetHandler(CreateJob("feature.cleanup"), out _));

        Assert.Contains("Multiple durable job feature handlers match job 'feature.cleanup'", exception.Message, StringComparison.Ordinal);
    }

    private static DurableJob CreateJob(string name) =>
        new()
        {
            Id = "job-1",
            Name = name,
            DueTime = DateTimeOffset.UtcNow,
            TargetGrainId = GrainId.Create("test", "grain-1"),
            ShardId = "shard-1"
        };

    private sealed class TestHandler(Func<DurableJob, bool> canHandle) : IDurableJobFeatureHandler
    {
        public bool CanHandle(DurableJob job) => canHandle(job);

        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(DurableJobRunResult.Completed);
    }
}
