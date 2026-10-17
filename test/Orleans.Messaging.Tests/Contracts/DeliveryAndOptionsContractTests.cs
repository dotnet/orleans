using Orleans.Messaging.Configuration;
using Polly;
using Xunit;

namespace Orleans.Messaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class DeliveryAndOptionsContractTests
{
    [Fact]
    public void RetentionTimeArithmetic_HandlesMaximumDurationsWithoutOverflow()
    {
        var timestamp = DateTimeOffset.MaxValue - TimeSpan.FromTicks(1);
        var fullDateTimeRange = TimeSpan.FromTicks(DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.MinValue.UtcTicks);

        Assert.False(MessagingTime.IsExpired(DateTimeOffset.MaxValue, timestamp, TimeSpan.MaxValue));
        Assert.True(MessagingTime.IsExpired(DateTimeOffset.MaxValue, DateTimeOffset.MinValue, fullDateTimeRange));
        Assert.False(MessagingTime.IsExpired(DateTimeOffset.MaxValue, DateTimeOffset.MinValue, TimeSpan.MaxValue));
        Assert.False(MessagingTime.IsExpired(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, TimeSpan.FromTicks(1)));
        Assert.Equal(DateTimeOffset.MaxValue, MessagingTime.AddClamped(timestamp, TimeSpan.MaxValue));
    }

    [Fact]
    public void DeadLetterCompaction_SaturatesMaximumRetentionWithoutOverflow()
    {
        var entries = new Dictionary<string, DateTimeOffset>
        {
            ["oldest"] = DateTimeOffset.MinValue,
            ["newest"] = DateTimeOffset.MaxValue
        };

        Assert.False(DeadLetterRetention.Compact(entries, DateTimeOffset.MaxValue, TimeSpan.MaxValue, int.MaxValue, static timestamp => timestamp));
        Assert.Equal(2, entries.Count);

        var fullDateTimeRange = TimeSpan.FromTicks(DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.MinValue.UtcTicks);
        Assert.True(DeadLetterRetention.Compact(entries, DateTimeOffset.MaxValue, fullDateTimeRange, int.MaxValue, static timestamp => timestamp));
        Assert.DoesNotContain("oldest", entries);
        Assert.Contains("newest", entries);
    }

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
        Assert.Equal([1, 2, 3, 4, 5], Enum.GetValues<DeliveryStatus>().Select(static value => (int)value));
        Assert.False(Enum.IsDefined(default(DeliveryResult).Status));
    }

    [Fact]
    public void Validate_DefaultOptions_SucceedsAndExposesDocumentedDefaults()
    {
        var options = new DurableMessagingOptions();

        options.Validate();

        Assert.Equal(1000, options.MaxCapacity);
        Assert.Equal(TimeSpan.FromDays(7), options.DeduplicationWindow);
        Assert.Equal(TimeSpan.FromHours(6), options.OutboxRetryTimeout);
        Assert.Equal(TimeSpan.FromDays(30), options.DeadLetterRetentionPeriod);
        Assert.Equal(5, options.MaxProcessingAttempts);
        Assert.Same(ResiliencePipeline.Empty, options.DeliveryResiliencePipeline);
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
            (nameof(DurableMessagingOptions.RetryInitialInterval), options => options.RetryInitialInterval = TimeSpan.Zero),
            (nameof(DurableMessagingOptions.RetryInitialInterval), options => options.RetryInitialInterval = TimeSpan.MaxValue),
            (nameof(DurableMessagingOptions.MaxProcessingAttempts), options => options.MaxProcessingAttempts = 0),
            (nameof(DurableMessagingOptions.OutboxRetryTimeout), options => options.OutboxRetryTimeout = TimeSpan.Zero),
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
    public void Validate_NullDeliveryResiliencePipeline_Fails()
    {
        var options = new DurableMessagingOptions { DeliveryResiliencePipeline = null! };

        var exception = Assert.Throws<ArgumentNullException>(options.Validate);
        Assert.Equal(nameof(DurableMessagingOptions.DeliveryResiliencePipeline), exception.ParamName);
    }

    [Fact]
    public void Validate_OutboxRetryTimeoutNotLessThanDeduplicationWindow_FailsAtBoundaryAndAbove()
    {
        foreach (var retryTimeout in new[] { TimeSpan.FromHours(2), TimeSpan.FromHours(3) })
        {
            var options = new DurableMessagingOptions
            {
                DeduplicationWindow = TimeSpan.FromHours(2),
                OutboxRetryTimeout = retryTimeout,
            };

            var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
            Assert.Equal(nameof(DurableMessagingOptions.OutboxRetryTimeout), exception.ParamName);
            Assert.Contains("less than DeduplicationWindow", exception.Message, StringComparison.Ordinal);
        }
    }
}
