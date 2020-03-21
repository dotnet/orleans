using System;

namespace Orleans.Versions.Compatibility
{
    public interface ICompatibilityDirector
    {
        bool IsCompatible(ushort requestedVersion, ushort currentVersion);
    }

    [Serializable]
    public abstract class CompatibilityStrategy
    {
    }
}