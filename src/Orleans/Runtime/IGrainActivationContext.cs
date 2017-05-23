using System;
using Orleans.Core;

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
    }
}