using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Testing;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.TestingHost;
using IOrderFulfillmentGrain = Documentation.Grains.DurableTasks.Versioning.IFulfillmentGrain;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.DurableTasks.Testing;

public interface IShippingRates
{
    decimal GetRate(string orderId);
}

public sealed class FixedShippingRates : IShippingRates
{
    public decimal GetRate(string orderId) => 10m;
}

public static class DurableTaskTestingSnippets
{
    // <durable_tasks_testing_capture>
    public static async Task CaptureAsync(InProcessTestCluster cluster, string orderId, string path)
    {
        // Run the workflow on the current code until it waits, for example for an approval signal.
        var fulfillment = cluster.Client.GetGrain<IOrderFulfillmentGrain>(orderId);
        await fulfillment.FulfillAsync(orderId).WithId($"fulfill-{orderId}").ScheduleAsync();

        // Capture the grain's committed journal from the silo's journal storage, and keep it with the tests.
        var storage = cluster.GetSiloServiceProvider().GetRequiredService<IJournalStorageProvider>();
        var history = await DurableTaskHistory.CaptureAsync(storage, fulfillment.GetGrainId());
        await history.SaveAsync(path);
    }
    // </durable_tasks_testing_capture>

    // <durable_tasks_testing_replay>
    public static async Task ReplayAsync(string path)
    {
        // The replay silo runs the test process's grain classes with the services which their constructors need.
        await using var replayer = await DurableTaskReplayer.StartAsync(
            silo => silo.Services.AddSingleton<IShippingRates, FixedShippingRates>());

        var history = await DurableTaskHistory.LoadAsync(path);
        var result = await replayer.ReplayAsync(history);

        // Throws with a report of each execution unless every one replays its recorded history consistently.
        result.EnsureSucceeded();
    }
    // </durable_tasks_testing_replay>

    // <durable_tasks_testing_assert>
    public static async Task ReplayVersionedChangeAsync(DurableTaskReplayer replayer, string path, string orderId)
    {
        var result = await replayer.ReplayAsync(await DurableTaskHistory.LoadAsync(path));
        var execution = result[TaskId.CreateRoot($"fulfill-{orderId}")];

        // The execution shipped through the warehouse before the change, so it keeps version 0, and waits for the signal
        // which the history doesn't hold.
        if (execution.Outcome != DurableTaskReplayOutcome.Waiting || execution.Versions["carrier-booking"] != 0)
        {
            throw new InvalidOperationException(result.ToString());
        }
    }
    // </durable_tasks_testing_assert>

    // <durable_tasks_testing_all>
    public static async Task ReplayAllAsync(string directory)
    {
        await using var replayer = await DurableTaskReplayer.StartAsync(
            silo => silo.Services.AddSingleton<IShippingRates, FixedShippingRates>());

        // One replayer replays every history in turn; each replay starts from its own history.
        var failures = new List<DurableTaskReplayResult>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var result = await replayer.ReplayAsync(await DurableTaskHistory.LoadAsync(path));
            if (!result.Succeeded)
            {
                failures.Add(result);
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }
    }
    // </durable_tasks_testing_all>
}
