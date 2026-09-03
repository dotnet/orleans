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
            Console.WriteLine($"Workflow successfully canceled (completed with OperationCanceledException).");
        }
    }

    [Alias("WorkflowsApp.Service.Samples.CancelWorld.CancelWorld.IBlockingGrain")]
    public interface IBlockingGrain : IGrainWithStringKey
    {
        [Alias("BlockUntilCanceled")]
        DurableTask BlockUntilCanceled(string input);
    }

    internal class BlockingGrain : DurableGrain, IBlockingGrain
    {
        public async DurableTask BlockUntilCanceled(string name)
        {
            var context = DurableExecutionContext.CurrentContext
                ?? throw new InvalidOperationException("A durable execution context is required.");
            using var cancellation = new CancellationTokenSource();
            using var registration = context.RegisterCancellationCallback(
                static (source, _) => source.CancelAsync(),
                cancellation);
            using var deactivationRegistration = context.RegisterDeactivationCallback(
                static (source, _) => source.CancelAsync(),
                cancellation);
            await Task.Delay(Timeout.Infinite, cancellation.Token);
        }
    }

    [Alias("WorkflowsApp.Service.Samples.CancelWorld.CancelWorld.IBlockingWorkflowGrain")]
    public interface IBlockingWorkflowGrain : IGrainWithStringKey
    {
        [Alias("RunSample")]
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
