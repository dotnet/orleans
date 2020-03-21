using System;
using System.Threading.Tasks;
using Orleans;
using ReplicatedEventSample.Interfaces;

namespace ReplicatedEventSample.Grains
{
    public class GeneratorGrain : Grain, IGeneratorGrain
    {
        private IEventGrain eventGrain;
        private Random random;
        private bool started;

        public Task Start()
        {
            // keep generating for at least 10 minutes
            DelayDeactivation(TimeSpan.FromMinutes(10));

            // use different deterministic pseudo-random sequence for each grain
            random = new Random((int) this.GetPrimaryKeyLong());

            if (!started)
            {
                started = true;

                // find event grain for this generator
                eventGrain = GrainFactory.GetGrain<IEventGrain>("event" + this.GetPrimaryKeyLong());

                RegisterTimer(Generate, null,
                    TimeSpan.FromSeconds(random.Next(20)), // start within 20 secs
                    TimeSpan.FromSeconds(2 + random.NextDouble())); // one outcome about every 2.5 seconds
            }

            return TaskDone.Done;
        }


        private async Task Generate(object _)
        {
            // wait 0-1 seconds
            await Task.Delay((int) (1000*random.NextDouble()));

            // pick random name and score for outcome
            var outcome = new Outcome()
            {
                Name = ((char) ('A' + random.Next(26))).ToString(),
                Score = random.Next(100),
                When = DateTime.UtcNow
            };

            // notify event grain of new outcome
            await eventGrain.NewOutcome(outcome);
        }
    }
}