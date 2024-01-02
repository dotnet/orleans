using System.Diagnostics;
using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;

namespace WorkflowsApp.Service.Samples.CancelWorld;

internal static class CancelWorld
{
    public static async Task RunAsync(IServiceProvider serviceProvider)
    {
        var grainFactory = serviceProvider.GetRequiredService<IGrainFactory>();
        var orchestrationGrain = grainFactory.GetGrain<IBlockingWorkflowGrain>("default");

        var instance = await orchestrationGrain.RunSample().ScheduleAsync();
        Console.WriteLine($"Started workflow '{instance.Id}'.");

        var status = await instance.GetStatusAsync(new PollingOptions { PollTimeout = TimeSpan.FromSeconds(5) });
        Debug.Assert(status == DurableTaskStatus.Pending);

        await instance.CancelAsync();

        status = await instance.GetStatusAsync(new PollingOptions { PollTimeout = TimeSpan.FromSeconds(5) });
        Debug.Assert(status == DurableTaskStatus.Canceled);

        // Block until the orchestration completes
        try
        {
            var result = await instance.WaitAsync();
            Debug.Fail("This should throw.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Workflow successfully canceled.");
        }
    }

    public interface IBlockingGrain : IGrainWithStringKey
    {
        DurableTask BlockUntilCanceled(string input);
    }

    internal class BlockingGrain : DurableGrain, IBlockingGrain
    {
        public DurableTask BlockUntilCanceled(string name) => DurableTask.Run(cancellation => Task.Delay(Timeout.Infinite, cancellation));
    }

    public interface IBlockingWorkflowGrain : IGrainWithStringKey
    {
        DurableTask<string> RunSample();
    }

    internal class CancelWorkflowGrain : DurableGrain, IBlockingWorkflowGrain
    {
        public async DurableTask<string> RunSample()
        {
            for (var i = 0; i < 5; i++)
            {
                var grain = GrainFactory.GetGrain<IBlockingGrain>($"stuck-{i}");
                await grain.BlockUntilCanceled($"Task {i}");
            }

            return "We did it!";
        }
    }
}
