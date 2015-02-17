using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using Orleans;

using System;

namespace LoadTestGrains
{
   public class StreamingBenchmarkConsumerGrain : Grain, IStreamingBenchmarkConsumerGrain
    {
        private StreamingBenchmarkConsumer _consumer;

        public override Task OnActivateAsync()
        {
            _consumer = new StreamingBenchmarkConsumer(msg => GetLogger().Info(msg), GetStreamProvider);
            return base.OnActivateAsync();
        }

        public Task StartConsumer(Guid streamId, string providerName)
        {
            return _consumer.StartConsumer(streamId, providerName);
        }

        public Task StopConsumer()
        {
            return _consumer.StopConsumer();
        }

        public Task SetVerbosity(double verbosity, long period)
        {
            return _consumer.SetVerbosity(verbosity, period);
        }
    }
}