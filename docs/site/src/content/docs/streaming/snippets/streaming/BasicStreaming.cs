using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Docs.Snippets.Streaming;

// <stream_contract>
[GenerateSerializer]
public sealed class TemperatureReading
{
    [Id(0)]
    public required double Celsius { get; init; }

    [Id(1)]
    public required DateTimeOffset ObservedAt { get; init; }
}

public interface ITemperatureProducerGrain : IGrainWithStringKey
{
    Task StartAsync();
}
// </stream_contract>

// <stream_identity>
public static class TemperatureStreams
{
    public const string ProviderName = "Telemetry";
    public const string Namespace = "device-telemetry";

    public static IAsyncStream<TemperatureReading> Get(
        Grain grain,
        string deviceId)
    {
        var provider = grain.GetStreamProvider(ProviderName);
        var streamId = StreamId.Create(Namespace, deviceId);
        return provider.GetStream<TemperatureReading>(streamId);
    }
}
// </stream_identity>

// <stream_producer>
public sealed class TemperatureProducerGrain : Grain, ITemperatureProducerGrain
{
    private IAsyncStream<TemperatureReading> _stream = null!;
    private IGrainTimer? _timer;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _stream = TemperatureStreams.Get(this, this.GetPrimaryKeyString());
        return Task.CompletedTask;
    }

    public Task StartAsync()
    {
        _timer ??= this.RegisterGrainTimer(
            PublishAsync,
            dueTime: TimeSpan.Zero,
            period: TimeSpan.FromSeconds(5));

        return Task.CompletedTask;
    }

    private Task PublishAsync(CancellationToken cancellationToken) =>
        _stream.OnNextAsync(new TemperatureReading
        {
            Celsius = Random.Shared.Next(-20, 45),
            ObservedAt = DateTimeOffset.UtcNow,
        });
}
// </stream_producer>
