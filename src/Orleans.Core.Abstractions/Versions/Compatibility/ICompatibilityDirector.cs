using System;

namespace Orleans.Versions.Compatibility
{
    public interface ICompatibilityDirector
    {
        bool IsCompatible(ushort requestedVersion, ushort currentVersion);
    }

    [Serializable]
    [GenerateSerializer]
    public abstract class CompatibilityStrategy
    {
    }
}