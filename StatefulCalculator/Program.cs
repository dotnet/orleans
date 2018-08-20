using System;
using System.Diagnostics;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Services.Runtime;
using Orleans.Clustering.ServiceFabric;
using Orleans.Clustering.ServiceFabric.Stateful;
using Orleans.Hosting;

namespace StatefulCalculator
{
    internal static class Program
    {
        /// <summary>
        /// This is the entry point of the service host process.
        /// </summary>
        private static void Main()
        {
            // Standard Service Fabric setup code
            ServiceRuntime.RegisterServiceAsync(
                    "StatefulCalculatorType",
                    // Register the silo
                    context => ActorService.Create(context, Configure, RunAsync))
                .GetAwaiter()
                .GetResult();

            Thread.Sleep(Timeout.Infinite);
        }

        // This is where we add plugins and configure the service
        private static void Configure(ActorService service, ISiloHostBuilder builder)
        {
            // Add state persistence using Service Fabric Reliable Collections
            builder.AddReliableDictionaryGrainStorage(service.StateManager);
            
            builder.ConfigureLogging(logging => logging.AddSeq());
        }

        // This is the standard RunAsync from the Reliable Services base class
        private static async Task RunAsync(ActorService service, CancellationToken cancellationToken)
        {
            //
            // Application code
            // 
            var client = await service.GetClient();

            var log = client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("App");
            var partitionKey = service.Partition.PartitionInfo.GetPartitionKeyString();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Get the grain
                    var grain = client.GetGrain<ICalculatorGrain>(Guid.Empty);

                    // Call the grain
                    var value = await grain.Add(1).WithTimeout(TimeSpan.FromSeconds(5));

                    log.LogInformation("[{PartitionKey}] value = {Value}, GrainProcessId: {GrainProcessId}", partitionKey, value, await grain.GetProcessId());
                }
                catch (Exception exception)
                {
                    log.LogWarning("[{PartitionKey}] Exception: {Exception}", partitionKey, exception);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    //
    // ALTERNATIVE - instead of the above, we can define our service using a base class.
    //

    public class CalculatorService : ActorService
    {
        protected CalculatorService(StatefulServiceContext context) : base(context)
        {
        }

        protected override void Configure(ISiloHostBuilder builder)
        {
            // Add state persistence using Service Fabric Reliable Collections
            builder.AddReliableDictionaryGrainStorage(this.StateManager);

            builder.ConfigureLogging(logging => logging.AddSeq());
        }

        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            //
            // Application code
            // 
            var client = await this.GetClient();
            
            var log = client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("App");
            var partitionKey = this.Partition.PartitionInfo.GetPartitionKeyString();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Get the grain
                    var grain = client.GetGrain<ICalculatorGrain>(Guid.Empty);

                    // Call the grain
                    var value = grain.Add(1).WithTimeout(TimeSpan.FromSeconds(5));
                    
                    log.LogInformation("[{PartitionKey}] value = {Value}, GrainProcessId: {GrainProcessId}", partitionKey, value, await grain.GetProcessId());
                }
                catch (Exception exception)
                {
                    log.LogWarning("[{PartitionKey}] Exception: {Exception}", partitionKey, exception);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }
}