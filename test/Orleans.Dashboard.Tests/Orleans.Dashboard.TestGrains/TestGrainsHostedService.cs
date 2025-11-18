using Microsoft.Extensions.Hosting;
using Orleans;
using System.Threading;
using System.Threading.Tasks;

namespace TestGrains
{
    public class TestGrainsHostedService : IHostedService
    {
        private readonly IGrainFactory grainFactory;
        private readonly CancellationTokenSource stop = new();

        public TestGrainsHostedService(IGrainFactory grainFactory)
        {
            this.grainFactory = grainFactory;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            TestCalls.Run(grainFactory, stop.Token);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            stop.Cancel();

            return Task.CompletedTask;
        }
    }
}
