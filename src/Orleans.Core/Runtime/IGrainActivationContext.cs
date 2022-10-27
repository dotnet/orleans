using System;
using Orleans.Core;
using System.Collections.Generic;
using Orleans.GrainDirectory;

namespace Orleans.Runtime
{
    /// <summary>
    /// The context associated with the current grain activation.
    /// </summary>
    public interface IGrainActivationContext
    {
        /// <summary>Gets the .NET type of the grain activation instance.</summary>
        Type GrainType { get; }

        /// <summary>Gets the identity of the grain activation.</summary>
        IGrainIdentity GrainIdentity { get; }

        /// <summary>Gets the <see cref="IServiceProvider"/> that provides access to the grain activation's service container.</summary>
        IServiceProvider ActivationServices { get; }

        /// <summary>Gets the instance of the grain associated with this activation context. 
        /// The value will be <see langword="null"/> if the grain is being created.</summary>
        Grain GrainInstance { get; }

        /// <summary>Gets a key/value collection that can be used to share data within the scope of the grain activation.</summary>
        IDictionary<object, object> Items { get; }

        /// <summary>
        /// Observable Grain life cycle
        /// </summary>
        IGrainLifecycle ObservableLifecycle { get; }
    }

    /// <summary>
    /// Provides access to the <see cref="IGrainActivationContext"/> of the currently executing grain activation.
    /// </summary>
    public interface IGrainActivationContextAccessor
    {
        /// <summary>
        /// Gets the <see cref="IGrainActivationContext"/> of the currently executing grain activation.
        /// </summary>
        /// <remarks>
        /// The resulting value will be <see langword="null"/> if the current execution context is not associated with a grain activation.
        /// </remarks>
        IGrainActivationContext GrainActivationContext { get; }
    }

    /// <summary>
    /// Provides access to the <see cref="IGrainActivationContext"/> of the currently executing grain activation.
    /// </summary>
    internal sealed class GrainActivationContextAccessor : IGrainActivationContextAccessor
    {
        /// <inheritdoc/>
        public IGrainActivationContext GrainActivationContext => RuntimeContext.CurrentGrainContext as IGrainActivationContext;
    }
}