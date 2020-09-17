using System;
using System.Threading.Tasks;
using Bond;
using Common;
using GrainInterfaces;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace Grains
{
    public class ProducerGrain : Grain, IProducerGrain
    {
        private readonly ILogger<IProducerGrain> logger;

        private IAsyncStream<int> stream;
        private IDisposable timer;

        private int counter = 0;

        public ProducerGrain(ILogger<IProducerGrain> logger)
        {
            this.logger = logger;
        }

        public Task StartProducing(string ns, Guid key)
        {
            if (this.timer != null)
                throw new Exception("This grain is already producing events");

            // Get the stream
            this.stream = base
                .GetStreamProvider(Constants.StreamProvider)
                .GetStream<int>(key, ns);

            // Register a timer that produce an event every second
            var period = TimeSpan.FromSeconds(1);
            this.timer = base.RegisterTimer(TimerTick, null, period, period);

            this.logger.LogInformation("I will produce a new event every {Period}", period);

            return Task.CompletedTask;
        }

        private async Task TimerTick(object _)
        {
            var value = counter++;
            this.logger.LogInformation("Sending event {EventNumber}", value);
            await this.stream.OnNextAsync(value);
        }

        public Task StopProducing()
        {
            if (this.stream != null)
            {
                this.timer.Dispose();
                this.timer = null;
                this.stream = null;
            }

            return Task.CompletedTask;
        }
    }
}
