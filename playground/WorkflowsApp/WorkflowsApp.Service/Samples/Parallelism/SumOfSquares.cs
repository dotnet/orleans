using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;

namespace WorkflowsApp.Service.Samples.Parallelism;

internal static class SumOfSquares
{
    public static async Task RunAsync(IServiceProvider serviceProvider)
    {
        var grainFactory = serviceProvider.GetRequiredService<IGrainFactory>();
        var orchestrationGrain = grainFactory.GetGrain<ISumOfSquaresGrain>("default");

        var instance = await orchestrationGrain.SumSquares(10).ScheduleAsync();
        Console.WriteLine($"Started workflow '{instance.Id}'.");

        // Block until the orchestration completes
        var result = await instance.WaitAsync();
        Console.WriteLine($"Workflow completed: {result}");
    }

    public interface ISquareGrain : IGrainWithIntegerKey
    {
        DurableTask<int> Square(int input);
    }

    internal class SquareGrain : DurableGrain, ISquareGrain
    {
        public DurableTask<int> Square(int value) => DurableTask.FromResult(value * value);
    }

    public interface ISumOfSquaresGrain : IGrainWithStringKey
    {
        DurableTask<int> SumSquares(int input);
    }

    internal class SumOfSquaresGrain : DurableGrain, ISumOfSquaresGrain
    {
        public async DurableTask<int> SumSquares(int input)
        {
            List<ScheduledTask<int>> tasks = [];
            for (var i = 1; i <= input; i++)
            {
                tasks.Add(await GrainFactory.GetGrain<ISquareGrain>(i).Square(i).WithId($"{i}").ScheduleAsync());
            }

            var sum = 0;
            foreach (var result in tasks)
            {
                sum += await result;
            }

            return sum;
        }
    }
}
