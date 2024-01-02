using System.Distributed.DurableTasks;
using Orleans.Journaling;

namespace WorkflowsApp.Service.Samples.HumanInTheLoop;

internal static class HumanInTheLoop
{
    public static async Task RunAsync(IServiceProvider serviceProvider)
    {
        var grainFactory = serviceProvider.GetRequiredService<IGrainFactory>();
        var orchestrationGrain = grainFactory.GetGrain<IGreeterGrain>("default");

        var instance = await orchestrationGrain.GetGreetingAsync().ScheduleAsync();
        Console.WriteLine($"Started greeter workflow '{instance.Id}'.");
        Console.WriteLine($"Navigate to /greet/<greeting> to set a greeting or 'cancel' to cancel the workflow.");

        try
        {
            var result = await instance.WaitAsync();
            Console.WriteLine($"Workflow completed with result: {result}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Workflow was canceled.");
        }
    }

    internal static void ConfigureApp(WebApplication app)
    {
        app.MapGet("/greet/{greeting}", async (IGrainFactory grainFactory, string greeting) =>
        {
            var greeter = grainFactory.GetGrain<IGreeterGrain>("default");
            if (greeting == "cancel")
            {
                await greeter.CancelAsync();
            }
            else
            {
                await greeter.SetGreetingAsync(greeting);
            }
        });
    }

    public interface IGreeterGrain : IGrainWithStringKey
    {
        ValueTask SetGreetingAsync(string greeting);
        ValueTask CancelAsync();
        DurableTask<string> GetGreetingAsync();
    }

    internal class GreeterGrain([FromKeyedServices("state")] IDurableTaskCompletionSource<string> state) : DurableGrain, IGreeterGrain
    {
        public DurableTask<string> GetGreetingAsync() => DurableTask.Run(state.Task.WaitAsync);

        public async ValueTask SetGreetingAsync(string greeting)
        {
            state.TrySetResult(greeting);
            await WriteStateAsync();
        }

        public async ValueTask CancelAsync()
        {
            state.TrySetCanceled();
            await WriteStateAsync();
        }
    }
}
