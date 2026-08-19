using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace Orleans.Docs.Snippets.Streaming;

public interface IExplicitTelemetryGrain : IGrainWithStringKey
{
    Task SubscribeAsync();

    Task UnsubscribeAsync();
}

// <explicit_subscription_grain>
public sealed class ExplicitTelemetryGrain :
    Grain,
    IExplicitTelemetryGrain,
    IAsyncObserver<TemperatureReading>
{
    private readonly ILogger<ExplicitTelemetryGrain> _logger;
    private IAsyncStream<TemperatureReading> _stream = null!;

    public ExplicitTelemetryGrain(ILogger<ExplicitTelemetryGrain> logger) =>
        _logger = logger;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _stream = TemperatureStreams.Get(this, this.GetPrimaryKeyString());

        var handles = await _stream.GetAllSubscriptionHandles();
        foreach (var handle in handles)
        {
            await handle.ResumeAsync(this);
        }
    }

    public async Task SubscribeAsync()
    {
        var handles = await _stream.GetAllSubscriptionHandles();
        if (handles.Count == 0)
        {
            await _stream.SubscribeAsync(this);
        }
    }

    public async Task UnsubscribeAsync()
    {
        var handles = await _stream.GetAllSubscriptionHandles();
        foreach (var handle in handles)
        {
            await handle.UnsubscribeAsync();
        }

        DeactivateOnIdle();
    }

    public Task OnNextAsync(
        TemperatureReading item,
        StreamSequenceToken? token = null)
    {
        _logger.LogInformation(
            "Received {Temperature} C at {ObservedAt}",
            item.Celsius,
            item.ObservedAt);
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        _logger.LogError(ex, "The telemetry subscription failed");
        return Task.CompletedTask;
    }
}
// </explicit_subscription_grain>
