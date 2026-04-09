using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility
{
    internal class AllVersionsCompatibilityDirector : ICompatibilityDirector
    {
        public bool IsCompatible(GrainInterfaceVersion requestedVersion, GrainInterfaceVersion currentVersion)
        {
            return true;
        }
    }
}
