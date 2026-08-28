using System.Reflection;
using System.Runtime.CompilerServices;
using Orleans.DurableMessaging.Configuration;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DeliveryAndOptionsContractTests
{
    [Fact]
    public void DeliveryResult_EachFactory_PreservesStatusAndPayload()
    {
        var routeMissing = DeliveryResult.RouteNotFound("orders/missing");
        var deadLettered = DeliveryResult.DeadLettered("poison body");

        Assert.Equal(DeliveryStatus.Accepted, DeliveryResult.Accepted().Status);
        Assert.Equal(DeliveryStatus.Duplicate, DeliveryResult.Duplicate().Status);
        Assert.Equal(DeliveryStatus.Backpressured, DeliveryResult.Backpressured().Status);
        Assert.Equal(DeliveryStatus.RouteNotFound, routeMissing.Status);
        Assert.Equal("No handler for route 'orders/missing'", routeMissing.Message);
        Assert.Equal(DeliveryStatus.DeadLettered, deadLettered.Status);
        Assert.Equal("poison body", deadLettered.Message);
    }

    [Fact]
    public void DeliveryStatus_AllValues_HaveStableDistinctValues()
    {
        Assert.Equal(
            [
                DeliveryStatus.Accepted,
                DeliveryStatus.Duplicate,
                DeliveryStatus.Backpressured,
                DeliveryStatus.RouteNotFound,
                DeliveryStatus.DeadLettered
            ],
            Enum.GetValues<DeliveryStatus>());
        Assert.Equal([0, 1, 2, 3, 6], Enum.GetValues<DeliveryStatus>().Select(static value => (int)value));
    }

    [Fact]
    public void Validate_DefaultOptions_SucceedsAndExposesDocumentedDefaults()
    {
        var options = new DurableInboxOptions();

        options.Validate();

        Assert.Equal(1000, options.MaxCapacity);
        Assert.Equal(TimeSpan.FromDays(7), options.DeduplicationWindow);
        Assert.Equal(TimeSpan.FromDays(1), options.MaxOutboxRetryAge);
        Assert.Equal(5, options.MaxProcessingAttempts);
        Assert.Equal(100, options.MaxDeliveryAttempts);
        Assert.Equal(32, options.InboxBatchSize);
        Assert.Equal(32, options.OutboxBatchSize);
    }

    [Fact]
    public void Validate_EachCapacityRetryDeadLetterAndBatchBoundary_EnforcesContract()
    {
        var invalidCases = new (string Parameter, Action<DurableInboxOptions> Mutate)[]
        {
            (nameof(DurableInboxOptions.MaxCapacity), options => options.MaxCapacity = 0),
            (nameof(DurableInboxOptions.DeduplicationWindow), options => options.DeduplicationWindow = TimeSpan.Zero),
            (nameof(DurableInboxOptions.BackpressureRetryDelay), options => options.BackpressureRetryDelay = TimeSpan.Zero),
            (nameof(DurableInboxOptions.BackpressureRetryDelay), options => options.BackpressureRetryDelay = TimeSpan.MaxValue),
            (nameof(DurableInboxOptions.MaxProcessingAttempts), options => options.MaxProcessingAttempts = 0),
            (nameof(DurableInboxOptions.MaxDeliveryAttempts), options => options.MaxDeliveryAttempts = 0),
            (nameof(DurableInboxOptions.MaxOutboxRetryAge), options => options.MaxOutboxRetryAge = TimeSpan.Zero),
            (nameof(DurableInboxOptions.InboxBatchSize), options => options.InboxBatchSize = 0),
            (nameof(DurableInboxOptions.OutboxBatchSize), options => options.OutboxBatchSize = 0),
        };

        foreach (var (parameter, mutate) in invalidCases)
        {
            var options = new DurableInboxOptions();
            mutate(options);
            var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
            Assert.Equal(parameter, exception.ParamName);
        }
    }

    [Fact]
    public void Validate_MaxOutboxRetryAgeNotLessThanDeduplicationWindow_FailsAtBoundaryAndAbove()
    {
        foreach (var retryAge in new[] { TimeSpan.FromHours(2), TimeSpan.FromHours(3) })
        {
            var options = new DurableInboxOptions
            {
                DeduplicationWindow = TimeSpan.FromHours(2),
                MaxOutboxRetryAge = retryAge,
            };

            var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
            Assert.Equal(nameof(DurableInboxOptions.MaxOutboxRetryAge), exception.ParamName);
            Assert.Contains("less than DeduplicationWindow", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InboxLifecycleStart_ObservesPreCanceledLifecycleToken()
    {
        var extensionType = typeof(IDurableInbox).Assembly.GetType(
            "Orleans.DurableMessaging.DurableInboxExtension",
            throwOnError: true)!;
        var extension = (ILifecycleObserver)RuntimeHelpers.GetUninitializedObject(extensionType);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => extension.OnStart(cancellation.Token));
    }

    [Fact]
    public async Task InboxLifecycleStart_CancellationInterruptsBlockedResume()
    {
        var extensionType = typeof(IDurableInbox).Assembly.GetType(
            "Orleans.DurableMessaging.DurableInboxExtension",
            throwOnError: true)!;
        var extension = (ILifecycleObserver)RuntimeHelpers.GetUninitializedObject(extensionType);
        extensionType.GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(extension, new SemaphoreSlim(0, 1));
        extensionType.GetField("_metricsActive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(extension, 1);
        using var cancellation = new CancellationTokenSource();

        var start = extension.OnStart(cancellation.Token);
        Assert.False(start.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
    }

    [Fact]
    public void PhysicalJobId_EncodesGrainTypeAndKeyBoundaries()
    {
        var ownershipType = typeof(IDurableInbox).Assembly.GetType(
            "Orleans.DurableMessaging.DurableMessagingJobOwnership",
            throwOnError: true)!;
        var createJobId = ownershipType.GetMethod(
            "CreateJobId",
            BindingFlags.Static | BindingFlags.Public)!;

        var first = (string)createJobId.Invoke(
            null,
            ["job", GrainId.Create("a/b", "c"), "epoch:1"])!;
        var second = (string)createJobId.Invoke(
            null,
            ["job", GrainId.Create("a", "b/c"), "epoch:1"])!;

        Assert.NotEqual(first, second);
    }
}
