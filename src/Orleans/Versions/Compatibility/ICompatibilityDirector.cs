using System;

namespace Orleans.Versions.Compatibility
{
    public interface ICompatibilityDirector
    {
        bool IsCompatible(ushort requestedVersion, ushort currentVersion);
    }

    public interface ICompatibilityDirector<TStrategy> : ICompatibilityDirector where TStrategy : CompatibilityStrategy
    {
    }

    [Serializable]
    public abstract class CompatibilityStrategy
    {
    }
}