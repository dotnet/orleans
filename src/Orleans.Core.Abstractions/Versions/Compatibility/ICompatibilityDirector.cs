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
        public static CompatibilityStrategy Parse(string str)
        {
            if (str.Equals(typeof(AllVersionsCompatible).Name))
                return AllVersionsCompatible.Singleton;
            if (str.Equals(typeof(BackwardCompatible).Name))
                return BackwardCompatible.Singleton;
            if (str.Equals(typeof(StrictVersionCompatible).Name))
                return StrictVersionCompatible.Singleton;
            return null;
        }
    }
}