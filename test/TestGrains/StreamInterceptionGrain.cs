using System.Reflection;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [ImplicitStreamSubscription("InterceptedStream")]
#pragma warning disable 618
    public class StreamInterceptionGrain : Grain, IStreamInterceptionGrain, IGrainInvokeInterceptor
#pragma warning restore 618
    {
        private int lastStreamValue;

        public async Task<object> Invoke(MethodInfo methodInfo, InvokeMethodRequest request, IGrainMethodInvoker invoker)
        {
            var initialLastStreamValue = this.lastStreamValue;
            var result = await invoker.Invoke(this, request);

            // If the last stream value changed after the invoke, then the stream must have produced a value, double
            // it for testing purposes.
            if (this.lastStreamValue != initialLastStreamValue)
            {
                this.lastStreamValue *= 2;
            }

            return result;
        }

        public override async Task OnActivateAsync()
        {
            var streams = this.GetStreamProvider("SMSProvider");
            var stream = streams.GetStream<int>(this.GetPrimaryKey(), "InterceptedStream");
            await stream.SubscribeAsync(
                (value, token) =>
                {
                    this.lastStreamValue = value;
                    return Task.CompletedTask;
                });
            await base.OnActivateAsync();
        }

        public Task<int> GetLastStreamValue() => Task.FromResult(this.lastStreamValue);
    }
}