using Xunit;

namespace Orleans.Dissemination.PerformanceHarness;

public sealed partial class MeasurementTests
{
    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(WrongPublishParameters))]
    [InlineData(typeof(WrongPublishResult))]
    public void PublisherApiReportsMissingOrIncompatiblePublication(Type publisherType)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PublisherApi.GetPublishMethod(publisherType));
        Assert.Contains(publisherType.FullName!, error.Message, StringComparison.Ordinal);
        Assert.Contains("Task PublishStatistics(CancellationToken)", error.Message, StringComparison.Ordinal);
        Assert.Contains("Update the performance harness runtime adapter", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(WrongTimerType))]
    public void PublisherApiReportsMissingOrIncompatibleTimer(Type publisherType)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PublisherApi.GetTimerField(publisherType));
        Assert.Contains(publisherType.FullName!, error.Message, StringComparison.Ordinal);
        Assert.Contains("IDisposable _publishTimer", error.Message, StringComparison.Ordinal);
        Assert.Contains("Update the performance harness runtime adapter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublisherApiPreservesPublicationTokenAndDisposedTimerReference()
    {
        var publisher = new CompatiblePublisher();
        using var cancellation = new CancellationTokenSource();
        var publishMethod = PublisherApi.GetPublishMethod(typeof(CompatiblePublisher));
        var timerField = PublisherApi.GetTimerField(typeof(CompatiblePublisher));
        var timer = Assert.IsType<PublisherTimer>(timerField.GetValue(publisher));
        timer.Dispose();
        await (Task)publishMethod.Invoke(publisher, [cancellation.Token])!;

        Assert.True(timer.Disposed);
        Assert.Same(timer, timerField.GetValue(publisher));
        Assert.Equal(cancellation.Token, publisher.LastToken);
    }

    private sealed class CompatiblePublisher
    {
        private readonly IDisposable _publishTimer = new PublisherTimer();
        public CancellationToken LastToken { get; private set; }

        internal Task PublishStatistics(CancellationToken cancellationToken)
        {
            Assert.True(((PublisherTimer)_publishTimer).Disposed);
            LastToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class PublisherTimer : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class WrongPublishParameters
    {
        internal Task PublishStatistics() => Task.FromResult(this);
    }

    private sealed class WrongPublishResult
    {
        internal ValueTask PublishStatistics(CancellationToken cancellationToken) =>
            new(Task.FromResult((this, cancellationToken)));
    }

    private sealed class WrongTimerType
    {
        private readonly int _publishTimer = 1;
        public int Timer => _publishTimer;
    }
}
