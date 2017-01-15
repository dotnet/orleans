using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// This is a markup interface for system targets.
    /// System target are internal runtime objects that share some behaivior with grains, but also impose certain restrictions. In particular:
    /// System target are asynchronusly addressable actors.
    /// Proxy class is being generated for ISystemTarget, just like for IGrain
    /// System target are scheduled by the runtime scheduler and follow turn based concurrency.
    /// </summary> 
    public interface ISystemTarget : IAddressable
    {
    }

    /// <summary>
    /// Internal interface implemented by SystemTarget classes to expose the necessary internal info that allows this.AsReference to for for SystemTarget's same as it does for a grain class.
    /// </summary>
    internal interface ISystemTargetBase
    {
        SiloAddress Silo { get; }
        GrainId GrainId { get; }
    }
}
