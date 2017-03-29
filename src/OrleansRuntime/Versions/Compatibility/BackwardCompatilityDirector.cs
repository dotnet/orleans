using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility
{
    internal class BackwardCompatilityDirector : IVersionCompatibilityDirector<BackwardCompatible>
    {
        public bool IsCompatible(ushort requestedVersion, ushort actualVersion)
        {
            return requestedVersion <= actualVersion;
        }
    }
}
