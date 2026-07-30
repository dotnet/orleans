using Orleans;
using Orleans.Streams;

namespace StreamsQuickstart;

// <receiver_grain>
[ImplicitStreamSubscription("RANDOMDATA")]
public class ReceiverGrain : Grain, IRandomReceiver
{
    public override async Task OnActivateAsync()
    {
        // <subscribe_events>
        // Create a GUID based on our GUID as a grain
        var guid = this.GetPrimaryKey();

        // Get one of the providers which we defined in config
        var streamProvider = GetStreamProvider("SMSProvider");

        // Get the reference to a stream
        var stream = streamProvider.GetStream<int>(guid, "RANDOMDATA");

        // Set our OnNext method to the lambda which simply prints the data.
        // This doesn't make new subscriptions, because we are using implicit
        // subscriptions via [ImplicitStreamSubscription].
        await stream.SubscribeAsync<int>(
            async (data, token) =>
            {
                Console.WriteLine(data);
                await Task.CompletedTask;
            });
        // </subscribe_events>

        await base.OnActivateAsync();
    }
}
// </receiver_grain>
