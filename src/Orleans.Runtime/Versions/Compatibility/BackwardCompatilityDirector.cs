using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility
{
    internal class BackwardCompatilityDirector : ICompatibilityDirector<BackwardCompatible>
    {
        public bool IsCompatible(ushort requestedVersion, ushort currentVersion)
        {
            return requestedVersion <= currentVersion;
        }
    }
}
