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

    [Alias("WorkflowsApp.Service.Samples.HumanInTheLoop.HumanInTheLoop.IGreeterGrain")]
    public interface IGreeterGrain : IGrainWithStringKey
    {
        [Alias("SetGreetingAsync")]
        ValueTask SetGreetingAsync(string greeting);
        [Alias("CancelAsync")]
        ValueTask CancelAsync();
        [Alias("GetGreetingAsync")]
        DurableTask<string> GetGreetingAsync();
    }

    internal class GreeterGrain([FromKeyedServices("state")] IDurableTaskCompletionSource<string> state) : DurableGrain, IGreeterGrain
    {
        public async DurableTask<string> GetGreetingAsync()
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
            return await state.Task.WaitAsync(cancellation.Token);
        }

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
