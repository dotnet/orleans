using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;

namespace WorkflowsApp.Service.Samples.HelloWorld;

internal static class HelloWorld
{
    public static async Task RunAsync(IServiceProvider serviceProvider)
    {
        var grainFactory = serviceProvider.GetRequiredService<IGrainFactory>();
        var orchestrationGrain = grainFactory.GetGrain<IHelloWorkflowGrain>("default");
        var instance = await orchestrationGrain.RunSample().ScheduleAsync();
        Console.WriteLine($"Started workflow '{instance.Id}'.");

        // Block until the orchestration completes
        var result = await instance.WaitAsync();
        Console.WriteLine($"Workflow completed:\n\t{string.Join("\n\t", result)}");
    }

    public interface IHelloGrain : IGrainWithStringKey
    {
        DurableTask<string> SayHelloAsync(string input);
    }

    internal class HelloGrain : DurableGrain, IHelloGrain
    {
        public DurableTask<string> SayHelloAsync(string name) => DurableTask.FromResult($"Hello, {name}!");
    }

    public interface IHelloWorkflowGrain : IGrainWithStringKey
    {
        DurableTask<string[]> RunSample();
    }

    internal class HelloWorkflowGrain : DurableGrain, IHelloWorkflowGrain
    {
        public async DurableTask<string[]> RunSample()
        {
            var helloGrain = GrainFactory.GetGrain<IHelloGrain>("default");
            var result1 = await helloGrain.SayHelloAsync("Melbourne");
            var result2 = await helloGrain.SayHelloAsync("Seattle");
            var result3 = await helloGrain.SayHelloAsync("Shanghai");

            // Return greetings as an array
            return [result1, result2, result3];
        }
    }
}
