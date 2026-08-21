using Orleans;
using Orleans.DurableJobs;
using Xunit;

namespace Tester.DurableJobs;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
public class DurableJobFeatureHandlerTests
{
    [Fact]
    public void Registry_UsesOrdinalNamesAndRejectsExactDuplicates()
    {
        var registry = new DurableJobHandlerRegistry();
        var lower = new TestHandler();
        var upper = new TestHandler();

        registry.Register("feature", lower);
        registry.Register("Feature", upper);

        Assert.True(registry.TryGetHandler("feature", out var resolvedLower));
        Assert.True(registry.TryGetHandler("Feature", out var resolvedUpper));
        Assert.Same(lower, resolvedLower);
        Assert.Same(upper, resolvedUpper);
        Assert.Throws<InvalidOperationException>(() => registry.Register("feature", new TestHandler()));
    }

    private sealed class TestHandler : IDurableJobFeatureHandler
    {
        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(DurableJobRunResult.Completed);
    }
}
