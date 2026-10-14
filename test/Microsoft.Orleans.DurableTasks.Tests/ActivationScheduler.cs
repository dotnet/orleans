using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Runs test code on a grain activation's scheduler, as a silo runs the durable task runtime.
/// </summary>
/// <remarks>
/// The runtime captures the current scheduler for each execution and relies on its activation running one work item
/// at a time. Running a test on an activation's <c>ActivationTaskScheduler</c> gives the runtime that guarantee, so the
/// test, the runtime, and every execution interleave only at awaits.
/// </remarks>
internal static class ActivationScheduler
{
    // Work which a test leaves queued on its activation, such as a background write, can run after the test completes,
    // so the activations' services live for the test process.
    private static readonly ServiceProvider Services = new ServiceCollection()
        .AddLogging()
        .AddMetrics()
        .AddSingleton<OrleansInstruments>()
        .AddSingleton<SchedulerInstruments>()
        .BuildServiceProvider();

    public static Task RunAsync(Func<Task> body)
    {
        var activation = Substitute.For<IGrainContext>();
        activation.ActivationServices.Returns(Services);
        var workItems = new WorkItemGroup(
            activation,
            Options.Create(new SchedulingOptions()),
            Services.GetRequiredService<SchedulerInstruments>());
        activation.Scheduler.Returns(workItems);

        return Task.Factory.StartNew(
            body,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            workItems.TaskScheduler).Unwrap();
    }
}
