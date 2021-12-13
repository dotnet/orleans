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

        /// <summary>Gets the instance of the grain associated with this activation context. 
        /// The value will be <see langword="null"/> if the grain is being created.</summary>
        IAddressable GrainInstance { get; }

        /// <summary>
        /// Gets the activation id.
        /// </summary>
        ActivationId ActivationId { get; }

        /// <summary>
        /// Gets the activation address.
        /// </summary>
        GrainAddress Address { get; }

        /// <summary>Gets the <see cref="IServiceProvider"/> that provides access to the grain activation's service container.</summary>
        IServiceProvider ActivationServices { get; }

        /// <summary>
        /// Observable Grain life cycle
        /// </summary>
        IGrainLifecycle ObservableLifecycle { get; }

        /// <summary>
        /// Sets the provided value as the component for type <typeparamref name="TComponent"/>.
        /// </summary>
        /// <typeparam name="TComponent">The type used to lookup this component.</typeparam>
        /// <param name="value">The component instance.</param>
        void SetComponent<TComponent>(TComponent value);

        void ReceiveMessage(object message);

        IWorkItemScheduler Scheduler { get; }
        PlacementStrategy PlacementStrategy { get; }

        void Activate(Dictionary<string, object> requestContext, CancellationToken? cancellationToken = default);
        void Deactivate(CancellationToken? cancellationToken = default);
        Task Deactivated { get; }
    }

    public static class GrainContextExtensions
    {
        public static Task DeactivateAsync(this IGrainContext grainContext, CancellationToken? cancellationToken = default)
        {
            grainContext.Deactivate(cancellationToken);
            return grainContext.Deactivated;
        }
    }

    internal interface ICollectibleGrainContext : IGrainContext
    {
        bool IsValid { get; }
        bool IsExemptFromCollection { get; }
        TimeSpan CollectionAgeLimit { get; }
        DateTime KeepAliveUntil { get; }
        DateTime CollectionTicket { get; set; }
        bool IsInactive { get; }
        bool IsStale();
        TimeSpan GetIdleness();
        void StartDeactivating();
        void DelayDeactivation(TimeSpan timeSpan);
    }

    internal interface IActivationData : ICollectibleGrainContext
    {
        IGrainRuntime GrainRuntime { get; }
    }

    internal interface IGrainTimerRegistry
    {
        void OnTimerCreated(IGrainTimer timer);
        void OnTimerDisposed(IGrainTimer timer);
    }

    public interface IWorkItemScheduler
    {
        void QueueAction(Action action);
        void QueueTask(Task task);
        void QueueWorkItem(IThreadPoolWorkItem workItem);
    }

    /// <summary>
    /// Provides access to the currently executing grain context.
    /// </summary>
    public interface IGrainContextAccessor
    {
        /// <summary>
        /// Returns the currently executing grain context.
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
        /// <typeparam name="TExtensionInterface">The grain extension interface.</typeparam>
        TExtensionInterface GetExtension<TExtensionInterface>() where TExtensionInterface : IGrainExtension;

        /// <summary>
        /// Binds an extension to an addressable object, if not already done.
        /// </summary>
        /// <typeparam name="TExtension">The type of the extension (e.g. StreamConsumerExtension).</typeparam>
        /// <typeparam name="TExtensionInterface">The public interface type of the implementation.</typeparam>
        /// <param name="newExtensionFunc">A factory function that constructs a new extension object.</param>
        /// <returns>A tuple, containing first the extension and second an addressable reference to the extension's interface.</returns>
        (TExtension, TExtensionInterface) GetOrSetExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : TExtensionInterface
            where TExtensionInterface : IGrainExtension;
    }
}
