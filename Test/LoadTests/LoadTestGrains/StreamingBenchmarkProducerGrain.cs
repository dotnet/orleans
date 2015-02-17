using System;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using Orleans;


namespace LoadTestGrains
{

    public class StreamingBenchmarkProducerGrain : Grain, IStreamingBenchmarkProducerGrain
    {
        private StreamingBenchmarkProducer _producer;

        public override Task OnActivateAsync()
        {
            _producer = new StreamingBenchmarkProducer(msg => GetLogger(msg), GetStreamProvider);
            return base.OnActivateAsync();
        }

        public Task StartProducer(Guid streamId, string providerName)
        {
            return _producer.StartProducer(streamId, providerName);
        }

        public Task StopProducer()
        {
            return _producer.StopProducer();
        }

        public Task Push(StreamingBenchmarkItem item)
        {
            return _producer.OnNextAsync(item);
        }

        public Task SetVerbosity(double verbosity, long period)
        {
            return _producer.SetVerbosity(verbosity, period);
        }
    }
}