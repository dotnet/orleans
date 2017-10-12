using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility
{
    internal class StrictVersionCompatibilityDirector : ICompatibilityDirector<StrictVersionCompatible>
    {
        public bool IsCompatible(ushort requestedVersion, ushort currentVersion)
        {
            return requestedVersion == currentVersion;
        }
    }
}