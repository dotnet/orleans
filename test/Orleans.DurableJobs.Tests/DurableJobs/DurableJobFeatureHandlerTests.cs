using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Concurrency;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

[TestCategory("BVT")]
public class DurableJobFeatureHandlerTests
{
    [Fact]
    public void Registry_RejectsDuplicateJobNames()
    {
        var registry = new DurableJobHandlerRegistry();
        var handler = new TestHandler();

        registry.Register("feature", handler, requiresTurnIsolation: true);

        Assert.Throws<InvalidOperationException>(() => registry.Register("feature", handler));
    }

    [Fact]
    public void RescheduleAt_CreatesDurableRescheduleResult()
    {
        var dueTime = DateTimeOffset.UtcNow.AddMinutes(1);

        var result = DurableJobRunResult.RescheduleAt(dueTime);

        Assert.True(result.IsRescheduleRequested);
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, result.Status);
        Assert.Equal(dueTime, result.RescheduleTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Registry_Register_ThrowsForNullEmptyOrWhitespaceJobName(string? jobName)
    {
        var registry = new DurableJobHandlerRegistry();
        var handler = new TestHandler();

        var expectedType = jobName is null ? typeof(ArgumentNullException) : typeof(ArgumentException);
        var ex = Record.Exception(() => registry.Register(jobName!, handler));

        Assert.NotNull(ex);
        Assert.IsType(expectedType, ex);
    }

    [Fact]
    public void Registry_Register_ThrowsArgumentNullException_ForNullHandler()
    {
        var registry = new DurableJobHandlerRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register("feature", null!));
    }

    [Fact]
    public void Registry_TryGetHandler_ReturnsTrueAndHandler_WhenRegistered()
    {
        var registry = new DurableJobHandlerRegistry();
        var handler = new TestHandler();
        registry.Register("feature", handler, requiresTurnIsolation: true);

        var found = registry.TryGetHandler("feature", out var resolved);

        Assert.True(found);
        Assert.Same(handler, resolved);
    }

    [Fact]
    public void Registry_TryGetHandler_ReturnsFalseAndNull_WhenJobNameNotRegistered()
    {
        var registry = new DurableJobHandlerRegistry();
        registry.Register("feature", new TestHandler(), requiresTurnIsolation: true);

        var found = registry.TryGetHandler("some-other-job", out var resolved);

        Assert.False(found);
        Assert.Null(resolved);
    }

    [Fact]
    public void InProgress_ThrowsArgumentOutOfRangeException_ForZeroDelay()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => DurableJobRunResult.InProgress(TimeSpan.Zero));
        Assert.Equal("delay", ex.ParamName);
    }

    [Fact]
    public void InProgress_ThrowsArgumentOutOfRangeException_ForNegativeDelay()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => DurableJobRunResult.InProgress(TimeSpan.FromSeconds(-1)));
        Assert.Equal("delay", ex.ParamName);
    }

    [Fact]
    public void Failed_ThrowsArgumentNullException_ForNullException()
    {
        Assert.Throws<ArgumentNullException>(() => DurableJobRunResult.Failed(null!));
    }

    [Fact]
    public void Completed_ReturnsSameSingletonInstance_OnEveryAccess()
    {
        var first = DurableJobRunResult.Completed;
        var second = DurableJobRunResult.Completed;

        Assert.Same(first, second);
        Assert.Equal(DurableJobRunStatus.Completed, first.Status);
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
    public async Task FeatureReceiver_ExecutesRegisteredHandlerToCompletion()
    {
        var registry = new DurableJobHandlerRegistry();
        var handler = new TestHandler();
        registry.Register("feature", handler, requiresTurnIsolation: true);
        var extension = new DurableJobFeatureReceiverExtension(registry, CreateShared());

        var result = await extension.TryHandleFeatureJobAsync(
            new TestJobRunContext("feature"),
            CancellationToken.None);

        Assert.Same(DurableJobRunResult.Completed, result);
        Assert.Equal(1, handler.ExecutionCount);
    }

    [Fact]
    public async Task FeatureReceiver_ReturnsNullForUserJob()
    {
        var extension = new DurableJobFeatureReceiverExtension(new DurableJobHandlerRegistry(), CreateShared());

        var result = await extension.TryHandleFeatureJobAsync(
            new TestJobRunContext("user-job"),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FeatureReceiver_RejectsInProgressFromTurnIsolatedHandler()
    {
        var registry = new DurableJobHandlerRegistry();
        registry.Register(
            "feature",
            new DelegateHandler(() => DurableJobRunResult.InProgress(TimeSpan.FromSeconds(1))),
            requiresTurnIsolation: true);
        var extension = new DurableJobFeatureReceiverExtension(registry, CreateShared());

        var result = await extension.TryHandleFeatureJobAsync(
            new TestJobRunContext("feature"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsFailed);
        Assert.Contains("returned InProgress", result.Exception!.Message);
    }

    [Theory]
    [InlineData(DurableJobRunStatus.Completed)]
    [InlineData(DurableJobRunStatus.InProgress)]
    [InlineData(DurableJobRunStatus.Failed)]
    [InlineData(DurableJobRunStatus.RescheduleRequested)]
    public void StatusFlags_AreMutuallyExclusive_AcrossAllStatuses(DurableJobRunStatus status)
    {
        var result = status switch
        {
            DurableJobRunStatus.Completed => DurableJobRunResult.Completed,
            DurableJobRunStatus.InProgress => DurableJobRunResult.InProgress(TimeSpan.FromSeconds(30)),
            DurableJobRunStatus.Failed => DurableJobRunResult.Failed(new InvalidOperationException("boom")),
            DurableJobRunStatus.RescheduleRequested => DurableJobRunResult.RescheduleAt(DateTimeOffset.UtcNow.AddMinutes(5)),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unexpected status."),
        };

        Assert.Equal(status, result.Status);

        var trueFlagCount = (result.IsFailed ? 1 : 0) + (result.IsInProgress ? 1 : 0) + (result.IsRescheduleRequested ? 1 : 0);

        switch (status)
        {
            case DurableJobRunStatus.Completed:
                Assert.Equal(0, trueFlagCount);
                Assert.False(result.IsFailed);
                Assert.False(result.IsInProgress);
                Assert.False(result.IsRescheduleRequested);
                break;
            case DurableJobRunStatus.InProgress:
                Assert.Equal(1, trueFlagCount);
                Assert.True(result.IsInProgress);
                Assert.False(result.IsFailed);
                Assert.False(result.IsRescheduleRequested);
                break;
            case DurableJobRunStatus.Failed:
                Assert.Equal(1, trueFlagCount);
                Assert.True(result.IsFailed);
                Assert.False(result.IsInProgress);
                Assert.False(result.IsRescheduleRequested);
                break;
            case DurableJobRunStatus.RescheduleRequested:
                Assert.Equal(1, trueFlagCount);
                Assert.True(result.IsRescheduleRequested);
                Assert.False(result.IsFailed);
                Assert.False(result.IsInProgress);
                break;
        }
    }

    private sealed class TestHandler : IDurableJobFeatureHandler
    {
        public int ExecutionCount { get; private set; }

        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.FromResult(DurableJobRunResult.Completed);
        }
    }

    private sealed class DelegateHandler(Func<DurableJobRunResult> resultFactory) : IDurableJobFeatureHandler
    {
        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(resultFactory());
    }

    private static DurableJobReceiverExtensionShared CreateShared() =>
        new(
            NullLogger<DurableJobReceiverExtension>.Instance,
            Options.Create(new DurableJobsOptions()),
            Options.Create(new SiloMessagingOptions()),
            TimeProvider.System);

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
