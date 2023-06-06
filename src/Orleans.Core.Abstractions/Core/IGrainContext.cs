using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a grain from the perspective of the runtime.
    /// </summary>
    public interface IGrainContext : ITargetHolder, IEquatable<IGrainContext>
    {
        /// <summary>
        /// Gets a reference to this grain.
        /// </summary>
        GrainReference GrainReference { get; }

        /// <summary>
        /// Gets the grain identity.
        /// </summary>
        GrainId GrainId { get; }

        /// <summary>
        /// Gets the grain instance, or <see langword="null"/> if the grain instance has not been set yet.
        /// </summary>
        object GrainInstance { get; }

        /// <summary>
        /// Gets the activation id.
        /// </summary>
        ActivationId ActivationId { get; }

        /// <summary>
        /// Gets the activation address.
        /// </summary>
        GrainAddress Address { get; }

        /// <summary>
        /// Gets the <see cref="IServiceProvider" /> that provides access to the grain activation's service container.
        /// </summary>
        IServiceProvider ActivationServices { get; }

        /// <summary>
        /// Gets the observable <see cref="Grain"/> lifecycle, which can be used to add lifecycle hooks.
        /// </summary>
        IGrainLifecycle ObservableLifecycle { get; }

        /// <summary>
        /// Gets the scheduler.
        /// </summary>
        IWorkItemScheduler Scheduler { get; }

        /// <summary>
        /// Gets the <see cref="Task"/> which completes when the grain has deactivated.
        /// </summary>
        Task Deactivated { get; }

        /// <summary>
        /// Sets the provided value as the component for type <typeparamref name="TComponent"/>.
        /// </summary>
        /// <typeparam name="TComponent">The type used to lookup this component.</typeparam>
        /// <param name="value">The component instance.</param>
        void SetComponent<TComponent>(TComponent value) where TComponent : class;

        /// <summary>
        /// Submits an incoming message to this instance.
        /// </summary>
        /// <param name="message">The message.</param>
        void ReceiveMessage(object message);

        /// <summary>
        /// Start activating this instance.
        /// </summary>
        /// <param name="requestContext">The request context of the request which is causing this instance to be activated, if any.</param>
        /// <param name="cancellationToken">A cancellation token which, when canceled, indicates that the process should complete promptly.</param>
        void Activate(Dictionary<string, object> requestContext, CancellationToken? cancellationToken = default);

        /// <summary>
        /// Start deactivating this instance.
        /// </summary>
        /// <param name="deactivationReason">The reason for deactivation, for informational purposes.</param>
        /// <param name="cancellationToken">A cancellation token which, when canceled, indicates that the process should complete promptly.</param>
        void Deactivate(DeactivationReason deactivationReason, CancellationToken? cancellationToken = default);

        /// <summary>
        /// Start rehydrating this instance from the provided rehydration context.
        /// </summary>
        void Rehydrate(IRehydrationContext context);

        /// <summary>
        /// Starts an attempt to migrating this instance to another location.
        /// Migration captures the current <see cref="RequestContext"/>, making it available to the activation's placement director so that it can consider it when selecting a new location.
        /// Migration will occur asynchronously, when no requests are executing, and will not occur if the activation's placement director does not select an alternative location.
        /// </summary>
        /// <param name="requestContext">The request context, which is provided to the placement director so that it can be examined when selecting a new location.</param>
        /// <param name="cancellationToken">A cancellation token which, when canceled, indicates that the process should complete promptly.</param>
        void Migrate(Dictionary<string, object> requestContext, CancellationToken? cancellationToken = default);
    }

    /// <summary>
    /// Extensions for <see cref="IGrainContext"/>.
    /// </summary>
    public static class GrainContextExtensions
    {
        /// <summary>
        /// Deactivates the provided grain.
        /// </summary>
        /// <param name="grainContext">
        /// The grain context.
        /// </param>
        /// <param name="deactivationReason">
        /// The deactivation reason.
        /// </param>
        /// <param name="cancellationToken">A cancellation token which when canceled, indicates that the process should complete promptly.</param>
        /// <returns>
        /// A <see cref="Task"/> which will complete once the grain has deactivated.
        /// </returns>
        public static Task DeactivateAsync(this IGrainContext grainContext, DeactivationReason deactivationReason, CancellationToken? cancellationToken = default)
        {
            grainContext.Deactivate(deactivationReason, cancellationToken);
            return grainContext.Deactivated;
        }
    }

    /// <summary>
    /// Defines functionality required for grains which are subject to activation collection.
    /// </summary>
    internal interface ICollectibleGrainContext : IGrainContext
    {
        /// <summary>
        /// Gets a value indicating whether the instance is available to process messages.
        /// </summary>
        bool IsValid { get; }

        /// <summary>
        /// Gets a value indicating whether this instance is exempt from collection.
        /// </summary>
        bool IsExemptFromCollection { get; }

        /// <summary>
        /// Gets a value indicating whether this instance is not currently processing a request.
        /// </summary>
        bool IsInactive { get; }

        /// <summary>
        /// Gets the collection age limit, which defines how long an instance must be inactive before it is eligible for collection.
        /// </summary>
        TimeSpan CollectionAgeLimit { get; }

        /// <summary>
        /// Gets the keep alive override value, which is the earliest time after which this instance will be available for collection.
        /// </summary>
        DateTime KeepAliveUntil { get; }

        /// <summary>
        /// Gets or sets the collection ticket, which is a special value used for tracking this activation's lifetime.
        /// </summary>
        DateTime CollectionTicket { get; set; }

        /// <summary>
        /// Gets a value indicating whether this activation has been idle longer than its <see cref="CollectionAgeLimit"/>.
        /// </summary>
        /// <returns><see langword="true"/> if the activation is stale, otherwise <see langword="false"/>.</returns>
        bool IsStale();

        /// <summary>
        /// Gets the duration which this activation has been idle for.
        /// </summary>
        /// <returns>
        /// The duration which this activation has been idle for.
        /// </returns>
        TimeSpan GetIdleness();

        /// <summary>
        /// Delays activation collection until at least until the specified duration has elapsed.
        /// </summary>
        /// <param name="timeSpan">The period of time to delay activation collection for.</param>
        void DelayDeactivation(TimeSpan timeSpan);
    }

    /// <summary>
    /// Provides functionality to record the creation and deletion of grain timers.
    /// </summary>
    internal interface IGrainTimerRegistry
    {
        /// <summary>
        /// Signals to the registry that a timer was created.
        /// </summary>
        /// <param name="timer">
        /// The timer.
        /// </param>
        void OnTimerCreated(IGrainTimer timer);

        /// <summary>
        /// Signals to the registry that a timer was disposed.
        /// </summary>
        /// <param name="timer">
        /// The timer.
        /// </param>
        void OnTimerDisposed(IGrainTimer timer);
    }

    /// <summary>
    /// Functionality to schedule tasks on a grain.
    /// </summary>
    public interface IWorkItemScheduler
    {
        /// <summary>
        /// Schedules an action for execution by this instance.
        /// </summary>
        /// <param name="action">
        /// The action.
        /// </param>
        void QueueAction(Action action);

        /// <summary>
        /// Schedules a task to be started by this instance.
        /// </summary>
        /// <param name="task">The task.</param>
        void QueueTask(Task task);

        /// <summary>
        /// Schedules a work item for execution by this instance.
        /// </summary>
        /// <param name="workItem">The work item.</param>
        void QueueWorkItem(IThreadPoolWorkItem workItem);
    }

    /// <summary>
    /// Provides access to the currently executing grain context.
    /// </summary>
    public interface IGrainContextAccessor
    {
        /// <summary>
        /// Gets the currently executing grain context.
        /// </summary>
        IGrainContext GrainContext { get; }
    }

    /// <summary>
    /// Functionality for accessing or installing an extension on a grain.
    /// </summary>
    public interface IGrainExtensionBinder
    {
        /// <summary>
        /// Returns the grain extension registered for the provided <typeparamref name="TExtensionInterface"/>.
        /// </summary>
        /// <typeparam name="TExtensionInterface">
        /// The grain extension interface.
        /// </typeparam>
        /// <returns>
        /// The implementation of the extension which is bound to this grain.
        /// </returns>
        TExtensionInterface GetExtension<TExtensionInterface>() where TExtensionInterface : class, IGrainExtension;

        /// <summary>
        /// Binds an extension to an addressable object, if not already done.
        /// </summary>
        /// <typeparam name="TExtension">The type of the extension (e.g. StreamConsumerExtension).</typeparam>
        /// <typeparam name="TExtensionInterface">The public interface type of the implementation.</typeparam>
        /// <param name="newExtensionFunc">A factory function that constructs a new extension object.</param>
        /// <returns>A tuple, containing first the extension and second an addressable reference to the extension's interface.</returns>
        (TExtension, TExtensionInterface) GetOrSetExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : class, TExtensionInterface
            where TExtensionInterface : class, IGrainExtension;
    }
}
