using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging.Configuration;
using Orleans.Serialization;
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
        var coded = DeliveryResult.DeadLettered("duplicate order", "orders/duplicate");
        var expired = DeliveryResult.Expired();

        Assert.Equal(DeliveryStatus.Accepted, DeliveryResult.Accepted().Status);
        Assert.Equal(DeliveryStatus.Duplicate, DeliveryResult.Duplicate().Status);
        Assert.Equal(DeliveryStatus.Backpressured, DeliveryResult.Backpressured().Status);
        Assert.Equal(DeliveryStatus.HandlerNotFound, handlerMissing.Status);
        Assert.Equal("No inbox handler is registered.", handlerMissing.Message);
        Assert.Equal(DeliveryStatus.DeadLettered, deadLettered.Status);
        Assert.Equal("poison body", deadLettered.Message);
        Assert.Null(deadLettered.Code);
        Assert.Equal("duplicate order", coded.Message);
        Assert.Equal("orders/duplicate", coded.Code);
        Assert.Equal(DeliveryStatus.Expired, expired.Status);
        Assert.Equal("The message was sent before the receiver's deduplication window; its earlier delivery outcome is unknown.", expired.Message);
        Assert.Null(expired.Code);
    }

    [Fact]
    public void DeliveryResult_Expired_RoundTripsStatusAndPayload()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<DeliveryResult>>();

        var decoded = serializer.Deserialize(serializer.SerializeToArray(DeliveryResult.Expired()));

        Assert.Equal(DeliveryStatus.Expired, decoded.Status);
        Assert.Equal("The message was sent before the receiver's deduplication window; its earlier delivery outcome is unknown.", decoded.Message);
        Assert.Null(decoded.Code);
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
                DeliveryStatus.DeadLettered,
                DeliveryStatus.Expired
            ],
            Enum.GetValues<DeliveryStatus>());
        Assert.Equal([1, 2, 3, 4, 5, 6], Enum.GetValues<DeliveryStatus>().Select(static value => (int)value));
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
            (nameof(DurableMessagingOptions.DeduplicationWindow), options => options.DeduplicationWindow = TimeSpan.FromTicks(-1)),
            (nameof(DurableMessagingOptions.RetryInitialInterval), options => options.RetryInitialInterval = TimeSpan.Zero),
            (nameof(DurableMessagingOptions.RetryInitialInterval), options => options.RetryInitialInterval = TimeSpan.MaxValue),
            (nameof(DurableMessagingOptions.OutboxRetryTimeout), options => options.OutboxRetryTimeout = TimeSpan.Zero),
            (nameof(DurableMessagingOptions.OutboxRetryTimeout), options => options.OutboxRetryTimeout = TimeSpan.FromTicks(-1)),
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

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(2, 2, false)]
    [InlineData(3, 2, false)]
    public void Validate_OutboxRetryTimeout_MustEndWithinTheDeduplicationWindow(long retryTimeoutTicks, long deduplicationWindowTicks, bool valid)
    {
        var options = new DurableMessagingOptions
        {
            DeduplicationWindow = TimeSpan.FromTicks(deduplicationWindowTicks),
            OutboxRetryTimeout = TimeSpan.FromTicks(retryTimeoutTicks),
        };

        if (valid)
        {
            options.Validate();
            return;
        }

        var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(nameof(DurableMessagingOptions.OutboxRetryTimeout), exception.ParamName);
        Assert.Contains("less than DeduplicationWindow", exception.Message, StringComparison.Ordinal);
    }
}
