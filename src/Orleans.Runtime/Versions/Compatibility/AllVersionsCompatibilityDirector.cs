using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility
{
    internal class AllVersionsCompatibilityDirector : ICompatibilityDirector
    {
        public bool IsCompatible(ushort requestedVersion, ushort currentVersion)
        {
            return true;
        }
    }
}