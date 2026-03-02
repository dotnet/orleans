using System.Collections.Concurrent;

namespace Orleans.TestingHost;

/// <summary>
/// Base class for test grains that need deterministic deactivation tracking.
/// Registers the <see cref="IGrainContext.Deactivated"/> task during activation so tests
/// can use <see cref="DeactivationTasks.WaitForDeactivationAsync"/> instead of arbitrary delays.
/// </summary>
/// <example>
/// <code>
/// // Define a test grain that inherits from DeactivationTestingGrain:
/// public class MyTestGrain : DeactivationTestingGrain, IMyTestGrain
/// {
///     public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
///         => Task.CompletedTask;
/// }
///
/// // In a test, wait for deactivation to complete:
/// var grain = grainFactory.GetGrain&lt;IMyTestGrain&gt;(0);
/// await grain.DoSomething();
/// await grain.Cast&lt;IGrainManagementExtension&gt;().DeactivateOnIdle();
/// await DeactivationTestingGrain.DeactivationTasks.WaitForDeactivationAsync(grain.GetGrainId());
/// </code>
/// </example>
/// <remarks>
/// <para>
/// Grains that already override <see cref="Grain.OnActivateAsync"/> can call
/// <see cref="DeactivationTasks.Register"/> directly instead of inheriting from this class.
/// </para>
/// </remarks>
public class DeactivationTestingGrain : Grain
{
    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        DeactivationTasks.Register(GrainContext);
        return base.OnActivateAsync(cancellationToken);
    }

    /// <summary>
    /// Tracks grain deactivation tasks keyed by <see cref="GrainId"/>, allowing tests
    /// to deterministically await deactivation instead of relying on arbitrary delays.
    /// </summary>
    public static class DeactivationTasks
    {
        private static readonly ConcurrentDictionary<GrainId, Task> Tasks = new();

        /// <summary>
        /// Registers the <see cref="IGrainContext.Deactivated"/> task for a grain.
        /// Call this from <see cref="Grain.OnActivateAsync"/> for grains that do not
        /// inherit from <see cref="DeactivationTestingGrain"/>.
        /// </summary>
        /// <param name="context">The grain context to track.</param>
        public static void Register(IGrainContext context) => Tasks[context.GrainId] = context.Deactivated;

        /// <summary>
        /// Waits for the grain with the specified <paramref name="grainId"/> to complete deactivation.
        /// </summary>
        /// <param name="grainId">The ID of the grain to wait for.</param>
        /// <param name="timeout">Maximum time to wait. Defaults to 5 seconds.</param>
        /// <returns>A task that completes when the grain has deactivated or the timeout expires.</returns>
        public static async Task WaitForDeactivationAsync(GrainId grainId, TimeSpan? timeout = null)
        {
            if (Tasks.TryRemove(grainId, out var task))
            {
                await task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
            }
        }
    }
}
