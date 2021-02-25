using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Internal interface implemented by the SystemTarget base class that enables generation of grain references for system targets.
    /// </summary>
    internal interface ISystemTargetBase
    {
        SiloAddress Silo { get; }
        GrainId GrainId { get; }
        GrainReference GrainReference { get; }
    }
}