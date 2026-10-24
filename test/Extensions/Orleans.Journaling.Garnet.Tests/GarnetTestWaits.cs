using Orleans.Journaling.Ranges;
using Xunit.Sdk;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Bounded waits for tests on slow CI runners: each fails with a description of the state it waited on.
/// </summary>
internal static class GarnetTestWaits
{
    /// <summary>The bound for one step of a test, such as a takeover and recovery or draining clients.</summary>
    public static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Awaits <paramref name="task"/>, failing with <paramref name="what"/> and the description <paramref name="describe"/>
    /// returns if it takes longer than <paramref name="timeout"/> (<see cref="StepTimeout"/> by default).
    /// </summary>
    public static async Task BoundedAsync(Task task, string what, Func<Task<string>> describe, TimeSpan? timeout = null)
    {
        var bound = timeout ?? StepTimeout;
        try
        {
            await task.WaitAsync(bound, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception) when (!task.IsCompleted)
        {
            throw new XunitException($"{what} did not complete within {bound}. {await DescribeSafelyAsync(describe)}", exception);
        }
    }

    /// <inheritdoc cref="BoundedAsync(Task, string, Func{Task{string}}, TimeSpan?)"/>
    public static async Task<T> BoundedAsync<T>(Task<T> task, string what, Func<Task<string>> describe, TimeSpan? timeout = null)
    {
        await BoundedAsync((Task)task, what, describe, timeout);
        return await task;
    }

    /// <summary>
    /// Reads the range map until <paramref name="condition"/> holds, failing with the latest map after <paramref name="timeout"/>.
    /// </summary>
    public static async Task<RangeMapSnapshot> WaitForMapAsync(IRangeMetaStore store, Func<RangeMap, bool> condition, string what, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        RangeMapSnapshot? view = null;
        while (true)
        {
            view = await BoundedAsync(store.ReadAsync(TestContext.Current.CancellationToken).AsTask(), $"Reading the range map while waiting until {what}", () => Task.FromResult(DescribeMap(view)));
            if (view is not null && condition(view.Map))
            {
                return view;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new XunitException($"The range map did not reach a state where {what} within {timeout}. {DescribeMap(view)}");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Describes a range map's hosting assignments and migrations, for failure messages.</summary>
    public static string DescribeMap(RangeMapSnapshot? view)
        => view is null
            ? "The MetaStore holds no map."
            : $"Map version {view.Version}: hosting [{string.Join("; ", view.Map.Partitions.Select(entry => $"{entry.Key}: host {entry.Value.Host}, epoch {entry.Value.Epoch}, endpoint {entry.Value.Endpoint ?? "none"}"))}]; "
                + $"migrations [{string.Join("; ", view.Map.Migrations)}].";

    private static async Task<string> DescribeSafelyAsync(Func<Task<string>> describe)
    {
        try
        {
            return await describe().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception)
        {
            return $"(Describing the state failed: {exception.Message})";
        }
    }
}
