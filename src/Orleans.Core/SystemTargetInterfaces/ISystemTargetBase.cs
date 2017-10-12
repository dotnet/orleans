using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Internal interface implemented by SystemTarget classes to expose the necessary internal info that allows this.AsReference to for for SystemTarget's same as it does for a grain class.
    /// </summary>
    internal interface ISystemTargetBase
    {
        SiloAddress Silo { get; }
        GrainId GrainId { get; }
        IRuntimeClient RuntimeClient { get; }
    }
}