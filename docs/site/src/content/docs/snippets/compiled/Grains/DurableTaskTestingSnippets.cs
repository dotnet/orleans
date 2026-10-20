using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Testing;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.TestingHost;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.DurableTasks.Testing;

public static class DurableTaskTestingSnippets
{
    // <durable_tasks_testing_capture>
    public static void ConfigureCaptureSilo(ISiloBuilder silo) =>
        silo.Services.AddSingleton(new FulfillmentCode { UseCarrier = false }).AddSingleton<FulfillmentProgress>();

    public static async Task CaptureAsync(InProcessTestCluster cluster, string orderId, string path)
    {
        var services = cluster.GetSiloServiceProvider();
        var progress = services.GetRequiredService<FulfillmentProgress>();
        var shipped = progress.WaitForShippingAsync(orderId);
        var fulfillment = cluster.Client.GetGrain<IOrderFulfillmentGrain>(orderId);
        await fulfillment.FulfillAsync(orderId).WithId($"fulfill-{orderId}").ScheduleAsync();
        await shipped.WaitAsync(TimeSpan.FromSeconds(30));

        // The workflow observes the shipping result after it commits, then waits for approval.
        var storage = services.GetRequiredService<IJournalStorageProvider>();
        var history = await DurableTaskHistory.CaptureAsync(storage, fulfillment.GetGrainId());
        await history.SaveAsync(path);
    }
    // </durable_tasks_testing_capture>

    // <durable_tasks_testing_replay>
    public static async Task ReplayAsync(string path)
    {
        // The replay silo runs the test process's grain classes with the services which their constructors need.
        await using var replayer = await DurableTaskReplayer.StartAsync(
            silo => silo.Services.AddSingleton<FulfillmentCode>().AddSingleton<FulfillmentProgress>());

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

        // The execution keeps the warehouse branch's version 0 and its pending approval.
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
            silo => silo.Services.AddSingleton<FulfillmentCode>().AddSingleton<FulfillmentProgress>());

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

// <durable_tasks_testing_workflow>
public interface IOrderFulfillmentGrain : IGrainWithStringKey
{
    DurableTask<string> FulfillAsync(string orderId);
}

public interface IShippingGrain : IGrainWithStringKey
{
    DurableTask<string> ShipAsync(string orderId);

    DurableTask<string> BookAsync(string orderId);
}

public sealed class FulfillmentCode
{
    public bool UseCarrier { get; init; } = true;
}

public sealed class OrderFulfillmentGrain(FulfillmentCode code, FulfillmentProgress progress) : DurableGrain, IOrderFulfillmentGrain
{
    private static readonly DurableSignal<string> Approved = new("approved");

    public async DurableTask<string> FulfillAsync(string orderId)
    {
        var shipping = GrainFactory.GetGrain<IShippingGrain>("main");
        var shipment = code.UseCarrier && DurableTask.GetVersion("carrier-booking", minSupported: 0, maxSupported: 1) >= 1
            ? await shipping.BookAsync(orderId).WithId("book")
            : await shipping.ShipAsync(orderId).WithId("ship");
        progress.Shipped(orderId);
        return $"{shipment}: {await Approved.WaitAsync()}";
    }
}

public sealed class ShippingGrain : DurableGrain, IShippingGrain
{
    public DurableTask<string> ShipAsync(string orderId) => DurableTask.FromResult($"shipped {orderId}");

    public DurableTask<string> BookAsync(string orderId) => DurableTask.FromResult($"booked {orderId}");
}

public sealed class FulfillmentProgress
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _shipping = new(StringComparer.Ordinal);

    public Task WaitForShippingAsync(string orderId) => GetShipping(orderId).Task;

    public void Shipped(string orderId) => GetShipping(orderId).TrySetResult();

    private TaskCompletionSource GetShipping(string orderId) =>
        _shipping.GetOrAdd(orderId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}
// </durable_tasks_testing_workflow>
