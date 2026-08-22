using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Runtime;
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
    public void Registry_RejectsAmbiguousMatchesAcrossIsolationModes()
    {
        var registry = new DurableJobHandlerRegistry();
        registry.Register(new TestHandler(static jobName => jobName.StartsWith("feature.", StringComparison.Ordinal)));
        registry.Register(
            new TestHandler(static jobName => jobName.EndsWith(".cleanup", StringComparison.Ordinal)),
            requiresTurnIsolation: true);

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.TryGetIsolatedHandler("feature.cleanup", out _));

        Assert.Contains("Multiple durable job feature handlers match job 'feature.cleanup'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FeatureReceiverMethod_IsNotAlwaysInterleavable()
    {
        var method = typeof(IDurableJobFeatureReceiverExtension).GetMethod(
            nameof(IDurableJobFeatureReceiverExtension.TryHandleFeatureJobAsync));

        Assert.NotNull(method);
        Assert.Null(method.GetCustomAttributes(typeof(AlwaysInterleaveAttribute), inherit: false).SingleOrDefault());
    }

    [Fact]
    public async Task FeatureReceiver_ExecutesRegisteredIsolatedHandlerToCompletion()
    {
        var registry = new DurableJobHandlerRegistry();
        var handler = new TestHandler(static jobName => jobName == "feature");
        registry.Register(handler, requiresTurnIsolation: true);
        var extension = new DurableJobFeatureReceiverExtension(registry, CreateShared());

        var result = await extension.TryHandleFeatureJobAsync(
            new TestJobRunContext("feature"),
            CancellationToken.None);

        Assert.Same(DurableJobRunResult.Completed, result);
        Assert.Equal(1, handler.ExecutionCount);
    }

    [Fact]
    public async Task FeatureReceiver_ReturnsNullForNonIsolatedHandler()
    {
        var registry = new DurableJobHandlerRegistry();
        registry.Register(new TestHandler(static jobName => jobName == "feature"));
        var extension = new DurableJobFeatureReceiverExtension(registry, CreateShared());

        var result = await extension.TryHandleFeatureJobAsync(
            new TestJobRunContext("feature"),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FeatureReceiver_RejectsInProgressFromTurnIsolatedHandler()
    {
        var registry = new DurableJobHandlerRegistry();
        registry.Register(
            new TestHandler(
                static jobName => jobName == "feature",
                static () => DurableJobRunResult.InProgress(TimeSpan.FromSeconds(1))),
            requiresTurnIsolation: true);
        var extension = new DurableJobFeatureReceiverExtension(registry, CreateShared());

        var result = await extension.TryHandleFeatureJobAsync(
            new TestJobRunContext("feature"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsFailed);
        Assert.Contains("returned InProgress", result.Exception!.Message);
    }

    private static DurableJobReceiverExtensionShared CreateShared() =>
        new(
            NullLogger<DurableJobReceiverExtension>.Instance,
            Options.Create(new DurableJobsOptions()),
            Options.Create(new SiloMessagingOptions()),
            TimeProvider.System);

    private sealed class TestHandler(
        Func<string, bool> canHandle,
        Func<DurableJobRunResult>? resultFactory = null) : IDurableJobFeatureHandler
    {
        public int ExecutionCount { get; private set; }

        public bool CanHandle(string jobName) => canHandle(jobName);

        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.FromResult(resultFactory?.Invoke() ?? DurableJobRunResult.Completed);
        }
    }

    private sealed class TestJobRunContext(string jobName) : IJobRunContext
    {
        public DurableJob Job { get; } = new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = jobName,
            DueTime = DateTimeOffset.UtcNow,
            TargetGrainId = GrainId.Create("test", Guid.NewGuid().ToString()),
            ShardId = "test"
        };

        public string RunId { get; } = Guid.NewGuid().ToString();

        public int DequeueCount => 0;
    }
}
