using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;

namespace WorkflowsApp.Service.Samples.HelloWorld;

public static class HelloWorld
{
    public static async Task RunAsync(IServiceProvider serviceProvider)
    {
        var grainFactory = serviceProvider.GetRequiredService<IGrainFactory>();
        var orchestrationGrain = grainFactory.GetGrain<IHelloWorkflowGrain>("default");
        var instance = await orchestrationGrain.RunSample().ScheduleAsync();
        Console.WriteLine($"Started workflow '{instance.Id}'.");

        var result = await instance.WaitAsync();
        Console.WriteLine($"Workflow completed:\n\t{string.Join("\n\t", result)}");
    }
}

[Alias("IHelloGrain")]
public interface IHelloGrain : IGrainWithStringKey
{
    [Alias("SayHelloAsync")]
    DurableTask<string> SayHelloAsync(string input);

    DurableTask WaitAsync();
}

internal class HelloGrain : DurableGrain, IHelloGrain
{
    public DurableTask<string> SayHelloAsync(string name) => DurableTask.FromResult($"Hello, {name}!");

    public DurableTask WaitAsync() => DurableTask.Delay(TimeSpan.FromMilliseconds(50));
}

[Alias("IHelloWorkflowGrain")]
public interface IHelloWorkflowGrain : IGrainWithStringKey
{
    [Alias("RunSample")]
    DurableTask<string[]> RunSample();
}

internal class HelloWorkflowGrain : DurableGrain, IHelloWorkflowGrain
{
    public async DurableTask<string[]> RunSample()
    {
        var helloGrain = GrainFactory.GetGrain<IHelloGrain>("default");
        await helloGrain.WaitAsync();
        var result1 = await helloGrain.SayHelloAsync("Melbourne");
        await DurableTask.Delay(TimeSpan.FromMilliseconds(500));
        var result2 = await helloGrain.SayHelloAsync("Seattle");
        var result3 = await helloGrain.SayHelloAsync("Shanghai");

        return [result1, result2, result3];
    }
}
