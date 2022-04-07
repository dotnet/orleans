using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [ImplicitStreamSubscription("InterceptedStream")]
    public class StreamInterceptionGrain : Grain, IStreamInterceptionGrain, IIncomingGrainCallFilter
    {
        private int lastStreamValue;

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            var streams = this.GetStreamProvider("SMSProvider");
            var stream = streams.GetStream<int>(this.GetPrimaryKey(), "InterceptedStream");
            await stream.SubscribeAsync(
                (value, token) =>
                {
                    this.lastStreamValue = value;
                    return Task.CompletedTask;
                });
            await base.OnActivateAsync(cancellationToken);
        }

        public Task<int> GetLastStreamValue() => Task.FromResult(this.lastStreamValue);

        public async Task Invoke(IIncomingGrainCallContext context)
        {
            var initialLastStreamValue = this.lastStreamValue;
            await context.Invoke();

            // If the last stream value changed after the invoke, then the stream must have produced a value, double
            // it for testing purposes.
            if (this.lastStreamValue != initialLastStreamValue)
            {
                this.lastStreamValue *= 2;
            }
        }
    }
}