using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace Orleans.Docs.Snippets.Streaming;

public interface IDeviceTelemetryGrain : IGrainWithStringKey
{
    Task<double?> GetLatestAsync();
}

// <implicit_subscription_grain>
[ImplicitStreamSubscription(TemperatureStreams.Namespace)]
public sealed class DeviceTelemetryGrain :
    Grain,
    IDeviceTelemetryGrain,
    IAsyncObserver<TemperatureReading>,
    IStreamSubscriptionObserver
{
    private readonly ILogger<DeviceTelemetryGrain> _logger;
    private double? _latest;

    public DeviceTelemetryGrain(ILogger<DeviceTelemetryGrain> logger) =>
        _logger = logger;

    public Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
    {
        var handle = handleFactory.Create<TemperatureReading>();
        return handle.ResumeAsync(this);
    }

    public Task OnNextAsync(
        TemperatureReading item,
        StreamSequenceToken? token = null)
    {
        _latest = item.Celsius;
        _logger.LogInformation(
            "Device {DeviceId} reported {Temperature} C",
            this.GetPrimaryKeyString(),
            item.Celsius);
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        _logger.LogError(ex, "The telemetry subscription failed");
        return Task.CompletedTask;
    }

    public Task<double?> GetLatestAsync() => Task.FromResult(_latest);
}
// </implicit_subscription_grain>
