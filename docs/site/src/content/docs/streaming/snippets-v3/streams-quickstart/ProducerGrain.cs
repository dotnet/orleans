using Orleans;
using Orleans.Streams;

namespace StreamsQuickstart;

// <producer_grain>
public class ProducerGrain : Grain, IRandomProducer
{
    private IAsyncStream<int>? _stream;

    public override Task OnActivateAsync()
    {
        // <produce_events>
        // Pick a GUID for a chat room grain and chat room stream
        var guid = new Guid("some guid identifying the chat room");
        // Get one of the providers which we defined in our config
        var streamProvider = GetStreamProvider("SMSProvider");
        // Get the reference to a stream
        var stream = streamProvider.GetStream<int>(guid, "RANDOMDATA");
        // </produce_events>

        _stream = stream;
        return base.OnActivateAsync();
    }

    public Task StartProducing()
    {
        RegisterTimer(_ =>
        {
            return _stream!.OnNextAsync(Random.Shared.Next());
        },
        null,
        TimeSpan.FromMilliseconds(1_000),
        TimeSpan.FromMilliseconds(1_000));

        return Task.CompletedTask;
    }
}
// </producer_grain>

public interface IRandomProducer : IGrainWithGuidKey
{
    Task StartProducing();
}
