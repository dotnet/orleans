using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility
{
    internal class BackwardCompatilityDirector : ICompatibilityDirector
    {
        public bool IsCompatible(GrainInterfaceVersion requestedVersion, GrainInterfaceVersion currentVersion)
        {
            return requestedVersion <= currentVersion;
        }
    }
}
