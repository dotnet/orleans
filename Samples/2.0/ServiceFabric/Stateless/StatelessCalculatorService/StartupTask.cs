using System;
using System.Threading;
using System.Threading.Tasks;
using GrainInterfaces;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace StatelessCalculatorService
{
    public class StartupTask : IStartupTask
    {
        private readonly IGrainFactory grainFactory;
        private readonly ILogger<StartupTask> logger;

        public StartupTask(IGrainFactory grainFactory, ILogger<StartupTask> logger)
        {
            this.grainFactory = grainFactory;
            this.logger = logger;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            // Message the grain repeatedly.
            var grain = this.grainFactory.GetGrain<ICalculatorGrain>(Guid.Empty);
            Task.Factory.StartNew(
                async () =>
                {
                    while (true)
                    {
                        try
                        {
                            var value = await grain.Add(1);
                            logger.Info($"{value - 1} + 1 = {value}");
                            await Task.Delay(TimeSpan.FromSeconds(4));
                        }
                        catch (Exception exception)
                        {
                            logger.Warn(exception.HResult, "Exception in bootstrap provider. Ignoring.", exception);
                        }
                    }
                }).Ignore();
            return Task.CompletedTask;
        }
    }
}