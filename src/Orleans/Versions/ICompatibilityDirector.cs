using System;
using System.Collections.Generic;

namespace Orleans.Versions.Compatibility
{
    public interface ICompatibilityDirector
    {
        IReadOnlyList<uint> GetCompatibleVersions(uint reqVersions, IReadOnlyList<uint> availableVersions);
    }

    public interface ICompatibilityDirector<TStrategy> : ICompatibilityDirector where TStrategy : CompatibilityStrategy
    {
    }

    [Serializable]
    public abstract class CompatibilityStrategy
    {
    }
}