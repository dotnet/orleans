using Orleans.Messaging.Configuration;
using Xunit;

namespace Orleans.Messaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class DeliveryAndOptionsContractTests
{
    [Fact]
    public void DeliveryResult_EachFactory_PreservesStatusAndPayload()
    {
        var handlerMissing = DeliveryResult.HandlerNotFound();
        var deadLettered = DeliveryResult.DeadLettered("poison body");

        Assert.Equal(DeliveryStatus.Accepted, DeliveryResult.Accepted().Status);
        Assert.Equal(DeliveryStatus.Duplicate, DeliveryResult.Duplicate().Status);
        Assert.Equal(DeliveryStatus.Backpressured, DeliveryResult.Backpressured().Status);
        Assert.Equal(DeliveryStatus.HandlerNotFound, handlerMissing.Status);
        Assert.Equal("No inbox handler is registered.", handlerMissing.Message);
        Assert.Equal(DeliveryStatus.DeadLettered, deadLettered.Status);
        Assert.Equal("poison body", deadLettered.Message);
    }

    [Fact]
    public void DeliveryStatus_AllValues_HaveContiguousDistinctValues()
    {
        Assert.Equal(
            [
                DeliveryStatus.Accepted,
                DeliveryStatus.Duplicate,
                DeliveryStatus.Backpressured,
                DeliveryStatus.HandlerNotFound,
                DeliveryStatus.DeadLettered
            ],
            Enum.GetValues<DeliveryStatus>());
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<DeliveryStatus>().Select(static value => (int)value));
    }

    [Fact]
    public void Validate_DefaultOptions_SucceedsAndExposesDocumentedDefaults()
    {
        var options = new DurableMessagingOptions();

        options.Validate();

        Assert.Equal(1000, options.MaxCapacity);
        Assert.Equal(TimeSpan.FromDays(7), options.DeduplicationWindow);
        Assert.Equal(TimeSpan.FromDays(1), options.MaxOutboxRetryAge);
        Assert.Equal(TimeSpan.FromDays(30), options.DeadLetterRetentionPeriod);
        Assert.Equal(5, options.MaxProcessingAttempts);
        Assert.Equal(100, options.MaxDeliveryAttempts);
        Assert.Equal(1000, options.MaxRetainedDeadLetters);
        Assert.Equal(32, options.InboxBatchSize);
        Assert.Equal(32, options.OutboxBatchSize);
    }

    [Fact]
    public void Validate_EachCapacityRetryDeadLetterAndBatchBoundary_EnforcesContract()
    {
        var invalidCases = new (string Parameter, Action<DurableMessagingOptions> Mutate)[]
        {
            (nameof(DurableMessagingOptions.MaxCapacity), options => options.MaxCapacity = 0),
            (nameof(DurableMessagingOptions.DeduplicationWindow), options => options.DeduplicationWindow = TimeSpan.Zero),
            (nameof(DurableMessagingOptions.BackpressureRetryDelay), options => options.BackpressureRetryDelay = TimeSpan.Zero),
            (nameof(DurableMessagingOptions.BackpressureRetryDelay), options => options.BackpressureRetryDelay = TimeSpan.MaxValue),
            (nameof(DurableMessagingOptions.MaxProcessingAttempts), options => options.MaxProcessingAttempts = 0),
            (nameof(DurableMessagingOptions.MaxDeliveryAttempts), options => options.MaxDeliveryAttempts = 0),
            (nameof(DurableMessagingOptions.MaxOutboxRetryAge), options => options.MaxOutboxRetryAge = TimeSpan.Zero),
            (nameof(DurableMessagingOptions.DeadLetterRetentionPeriod), options => options.DeadLetterRetentionPeriod = TimeSpan.Zero),
            (nameof(DurableMessagingOptions.MaxRetainedDeadLetters), options => options.MaxRetainedDeadLetters = 0),
            (nameof(DurableMessagingOptions.InboxBatchSize), options => options.InboxBatchSize = 0),
            (nameof(DurableMessagingOptions.OutboxBatchSize), options => options.OutboxBatchSize = 0),
        };

        foreach (var (parameter, mutate) in invalidCases)
        {
            var options = new DurableMessagingOptions();
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
            var options = new DurableMessagingOptions
            {
                DeduplicationWindow = TimeSpan.FromHours(2),
                MaxOutboxRetryAge = retryAge,
            };

            var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
            Assert.Equal(nameof(DurableMessagingOptions.MaxOutboxRetryAge), exception.ParamName);
            Assert.Contains("less than DeduplicationWindow", exception.Message, StringComparison.Ordinal);
        }
    }
}
