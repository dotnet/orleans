using System;

namespace Orleans.Versions.Compatibility
{
    public interface IVersionCompatibilityDirector
    {
        bool IsCompatible(ushort requestedVersion, ushort currentVersion);
    }

    public interface IVersionCompatibilityDirector<TStrategy> : IVersionCompatibilityDirector where TStrategy : VersionCompatibilityStrategy
    {
    }

    [Serializable]
    public abstract class VersionCompatibilityStrategy
    {
    }
}