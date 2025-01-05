using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Timers;

namespace Orleans
{
    /// <summary>
    /// Interface for grain implementations
    /// </summary>
    public interface IGrainBase
    {
        /// <summary>
        /// Gets the grain context.
        /// </summary>
        IGrainContext GrainContext { get; }

        /// <summary>
        /// Method overridden by grain implementations to handle activation.
        /// </summary>
        /// <param name="token">The cancellation token used to signify that activation should abort promptly.</param>
        /// <returns>A <see cref="Task"/> which represents the operation.</returns>
        Task OnActivateAsync(CancellationToken token) => Task.CompletedTask;

        /// <summary>
        /// Method overridden by grain implementations to handle deactivation.
        /// </summary>
        /// <param name="reason">The reason for deactivation.</param>
        /// <param name="token">The cancellation token used to signify that deactivation should complete promptly.</param>
        /// <returns>A <see cref="Task"/> which represents the operation.</returns>
        Task OnDeactivateAsync(DeactivationReason reason, CancellationToken token) => Task.CompletedTask;
    }

    /// <summary>
    /// Helper methods for <see cref="IGrainBase"/> implementations.
    /// </summary>
    public static class GrainBaseExtensions
    {
        /// <summary>
        /// Deactivate this grain activation after the current grain method call is completed.
        /// This call will mark this activation of the current grain to be deactivated and removed at the end of the current method.
        /// The next call to this grain will result in a different activation to be used, which typical means a new activation will be created automatically by the runtime.
        /// </summary>
        public static void DeactivateOnIdle(this IGrainBase grain) =>
            grain.GrainContext.Deactivate(new(DeactivationReasonCode.ApplicationRequested, $"{nameof(DeactivateOnIdle)} was called."));

        /// <summary>
        /// Starts an attempt to migrating this instance to another location.
        /// Migration captures the current <see cref="RequestContext"/>, making it available to the activation's placement director so that it can consider it when selecting a new location.
        /// Migration will occur asynchronously, when no requests are executing, and will not occur if the activation's placement director does not select an alternative location.
        /// </summary>
        public static void MigrateOnIdle(this IGrainBase grain) => grain.GrainContext.Migrate(RequestContext.CallContextData?.Value.Values);

        /// <summary>
        /// Creates a grain timer.
        /// </summary>
        /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
        /// <param name="state">The state passed to the callback.</param>
        /// <param name="options">
        /// The options for creating the timer.
        /// </param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        /// <returns>
        /// The <see cref="IGrainTimer"/> instance which represents the timer.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Grain timers do not keep grains active by default. Setting <see cref="GrainTimerCreationOptions.KeepAlive"/> to
        /// <see langword="true"/> causes each timer tick to extend the grain activation's lifetime.
        /// If the timer ticks are infrequent, the grain can still be deactivated due to idleness.
        /// When a grain is deactivated, all active timers are discarded.
        /// </para>
        /// <para>
        /// Until the <see cref="Task"/> returned from the callback is resolved, the next timer tick will not be scheduled.
        /// That is to say, a timer callback will never be concurrently executed with itself.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="true"/>, the timer callback will be allowed
        /// to interleave with with other grain method calls and other timers.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="false"/>, the timer callback will respect the
        /// reentrancy setting of the grain, just like a typical grain method call.
        /// </para>
        /// <para>
        /// The timer may be stopped at any time by calling the <see cref="IGrainTimer"/>'s <see cref="IDisposable.Dispose"/> method.
        /// Disposing a timer prevents any further timer ticks from being scheduled.
        /// </para>
        /// <para>
        /// The timer due time and period can be updated by calling its <see cref="IGrainTimer.Change(TimeSpan, TimeSpan)"/> method.
        /// Each time the timer is updated, the next timer tick will be scheduled based on the updated due time.
        /// Subsequent ticks will be scheduled after the updated period elapses.
        /// Note that this behavior is the same as the <see cref="Timer.Change(TimeSpan, TimeSpan)"/> method.
        /// </para>
        /// <para>
        /// Exceptions thrown from the callback will be logged, but will not prevent the next timer tick from being queued.
        /// </para>
        /// </remarks>
        public static IGrainTimer RegisterGrainTimer<TState>(this IGrainBase grain, Func<TState, CancellationToken, Task> callback, TState state, GrainTimerCreationOptions options)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (grain is Grain grainClass)
            {
                ArgumentNullException.ThrowIfNull(callback);

                grainClass.EnsureRuntime();
                return grainClass.Runtime.TimerRegistry.RegisterGrainTimer(grainClass.GrainContext, callback, state, options);
            }

            return grain.GrainContext.ActivationServices.GetRequiredService<ITimerRegistry>().RegisterGrainTimer(grain.GrainContext, callback, state, options);
        }

        /// <summary>
        /// Creates a grain timer.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
        /// <param name="options">
        /// The options for creating the timer.
        /// </param>
        /// <returns>
        /// The <see cref="IGrainTimer"/> instance which represents the timer.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Grain timers do not keep grains active by default. Setting <see cref="GrainTimerCreationOptions.KeepAlive"/> to
        /// <see langword="true"/> causes each timer tick to extend the grain activation's lifetime.
        /// If the timer ticks are infrequent, the grain can still be deactivated due to idleness.
        /// When a grain is deactivated, all active timers are discarded.
        /// </para>
        /// <para>
        /// Until the <see cref="Task"/> returned from the callback is resolved, the next timer tick will not be scheduled.
        /// That is to say, a timer callback will never be concurrently executed with itself.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="true"/>, the timer callback will be allowed
        /// to interleave with with other grain method calls and other timers.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="false"/>, the timer callback will respect the
        /// reentrancy setting of the grain, just like a typical grain method call.
        /// </para>
        /// <para>
        /// The timer may be stopped at any time by calling the <see cref="IGrainTimer"/>'s <see cref="IDisposable.Dispose"/> method.
        /// Disposing a timer prevents any further timer ticks from being scheduled.
        /// </para>
        /// <para>
        /// The timer due time and period can be updated by calling its <see cref="IGrainTimer.Change(TimeSpan, TimeSpan)"/> method.
        /// Each time the timer is updated, the next timer tick will be scheduled based on the updated due time.
        /// Subsequent ticks will be scheduled after the updated period elapses.
        /// Note that this behavior is the same as the <see cref="Timer.Change(TimeSpan, TimeSpan)"/> method.
        /// </para>
        /// <para>
        /// Exceptions thrown from the callback will be logged, but will not prevent the next timer tick from being queued.
        /// </para>
        /// </remarks>
        public static IGrainTimer RegisterGrainTimer(this IGrainBase grain, Func<CancellationToken, Task> callback, GrainTimerCreationOptions options)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RegisterGrainTimer(grain, static (callback, cancellationToken) => callback(cancellationToken), callback, options);
        }

        public static IGrainTimer RegisterGrainTimer(this IGrainBase grain, Func<Task> callback, GrainTimerCreationOptions options)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RegisterGrainTimer(grain, static (callback, cancellationToken) => callback(), callback, options);
        }

        /// <inheritdoc cref="RegisterGrainTimer(IGrainBase, Func{Task}, GrainTimerCreationOptions)"/>
        /// <param name="state">The state passed to the callback.</param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        public static IGrainTimer RegisterGrainTimer<TState>(this IGrainBase grain, Func<TState, Task> callback, TState state, GrainTimerCreationOptions options)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return RegisterGrainTimer(grain, static (state, _) => state.Callback(state.State), (Callback: callback, State: state), options);
        }

        /// <summary>
        /// Creates a grain timer.
        /// </summary>
        /// <param name="grain">The grain instance.</param>
        /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
        /// <param name="dueTime">
        /// A <see cref="TimeSpan"/> representing the amount of time to delay before invoking the callback method specified when the <see cref="IGrainTimer"/> was constructed.
        /// Specify <see cref="Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
        /// Specify <see cref="TimeSpan.Zero"/> to start the timer immediately.
        /// </param>
        /// <param name="period">
        /// The time interval between invocations of the callback method specified when the <see cref="IGrainTimer"/> was constructed.
        /// Specify <see cref="Timeout.InfiniteTimeSpan "/> to disable periodic signaling.
        /// </param>
        /// <returns>
        /// The <see cref="IGrainTimer"/> instance which represents the timer.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Grain timers do not keep grains active by default. Setting <see cref="GrainTimerCreationOptions.KeepAlive"/> to
        /// <see langword="true"/> causes each timer tick to extend the grain activation's lifetime.
        /// If the timer ticks are infrequent, the grain can still be deactivated due to idleness.
        /// When a grain is deactivated, all active timers are discarded.
        /// </para>
        /// <para>
        /// Until the <see cref="Task"/> returned from the callback is resolved, the next timer tick will not be scheduled.
        /// That is to say, a timer callback will never be concurrently executed with itself.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="true"/>, the timer callback will be allowed
        /// to interleave with with other grain method calls and other timers.
        /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="false"/>, the timer callback will respect the
        /// reentrancy setting of the grain, just like a typical grain method call.
        /// </para>
        /// <para>
        /// The timer may be stopped at any time by calling the <see cref="IGrainTimer"/>'s <see cref="IDisposable.Dispose"/> method.
        /// Disposing a timer prevents any further timer ticks from being scheduled.
        /// </para>
        /// <para>
        /// The timer due time and period can be updated by calling its <see cref="IGrainTimer.Change(TimeSpan, TimeSpan)"/> method.
        /// Each time the timer is updated, the next timer tick will be scheduled based on the updated due time.
        /// Subsequent ticks will be scheduled after the updated period elapses.
        /// Note that this behavior is the same as the <see cref="Timer.Change(TimeSpan, TimeSpan)"/> method.
        /// </para>
        /// <para>
        /// Exceptions thrown from the callback will be logged, but will not prevent the next timer tick from being queued.
        /// </para>
        /// </remarks>
        public static IGrainTimer RegisterGrainTimer(this IGrainBase grain, Func<Task> callback, TimeSpan dueTime, TimeSpan period)
            => RegisterGrainTimer(grain, callback, new() { DueTime = dueTime, Period = period });

        /// <inheritdoc cref="RegisterGrainTimer(IGrainBase, Func{Task}, TimeSpan, TimeSpan)"/>
        public static IGrainTimer RegisterGrainTimer(this IGrainBase grain, Func<CancellationToken, Task> callback, TimeSpan dueTime, TimeSpan period)
            => RegisterGrainTimer(grain, callback, new() { DueTime = dueTime, Period = period });

        /// <inheritdoc cref="RegisterGrainTimer(IGrainBase, Func{Task}, TimeSpan, TimeSpan)"/>
        /// <param name="state">The state passed to the callback.</param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        public static IGrainTimer RegisterGrainTimer<TState>(this IGrainBase grain, Func<TState, Task> callback, TState state, TimeSpan dueTime, TimeSpan period)
            => RegisterGrainTimer(grain, callback, state, new() { DueTime = dueTime, Period = period });

        /// <inheritdoc cref="RegisterGrainTimer(IGrainBase, Func{Task}, TimeSpan, TimeSpan)"/>
        /// <param name="state">The state passed to the callback.</param>
        /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
        public static IGrainTimer RegisterGrainTimer<TState>(this IGrainBase grain, Func<TState, CancellationToken, Task> callback, TState state, TimeSpan dueTime, TimeSpan period)
            => RegisterGrainTimer(grain, callback, state, new() { DueTime = dueTime, Period = period });
    }

    /// <summary>
    /// An informational reason code for deactivation.
    /// </summary>
    [GenerateSerializer]
    public enum DeactivationReasonCode : byte
    {
        /// <summary>
        /// No reason provided.
        /// </summary>
        None,

        /// <summary>
        /// The process is currently shutting down.
        /// </summary>
        ShuttingDown,

        /// <summary>
        /// Activation of the grain failed.
        /// </summary>
        ActivationFailed,

        /// <summary>
        /// This activation is affected by an internal failure in the distributed grain directory.
        /// </summary>
        /// <remarks>
        /// This could be caused by the failure of a process hosting this activation's grain directory partition, for example.
        /// </remarks>
        DirectoryFailure,

        /// <summary>
        /// This activation is idle.
        /// </summary>
        ActivationIdle,

        /// <summary>
        /// This activation is unresponsive to commands or requests.
        /// </summary>
        ActivationUnresponsive,

        /// <summary>
        /// Another instance of this grain has been activated.
        /// </summary>
        DuplicateActivation,

        /// <summary>
        /// This activation received a request which cannot be handled by the locally running process.
        /// </summary>
        IncompatibleRequest,

        /// <summary>
        /// An application error occurred.
        /// </summary>
        ApplicationError,

        /// <summary>
        /// The application requested to deactivate this activation.
        /// </summary>
        ApplicationRequested,

        /// <summary>
        /// This activation is migrating to a new location.
        /// </summary>
        Migrating,

        /// <summary>
        /// The runtime requested to deactivate this activation.
        /// </summary>
        RuntimeRequested
    }

    internal static class DeactivationReasonCodeExtensions
    {
        public static bool IsTransientError(this DeactivationReasonCode reasonCode)
        {
            return reasonCode is DeactivationReasonCode.DirectoryFailure;
        }
    }
}
