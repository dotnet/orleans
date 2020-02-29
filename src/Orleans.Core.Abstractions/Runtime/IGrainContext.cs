
using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a grain from the perpsective of the runtime.
    /// </summary>
    internal interface IGrainContext : IEquatable<IGrainContext>
    {
        GrainReference GrainReference { get; }
        GrainId GrainId { get; }
        IAddressable GrainInstance { get; }
        ActivationId ActivationId { get; }
        ActivationAddress Address { get; }
    }

    internal interface IActivationData : IGrainContext
    {
        IServiceProvider ServiceProvider { get; }
        void DelayDeactivation(TimeSpan timeSpan);
        void OnTimerCreated(IGrainTimer timer);
        void OnTimerDisposed(IGrainTimer timer);
    }
}
