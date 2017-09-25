using System;

namespace Orleans.Versions.Compatibility
{
    [Serializable]
    public class BackwardCompatible : CompatibilityStrategy
    {
        public static BackwardCompatible Singleton { get; } = new BackwardCompatible();

        private BackwardCompatible()
        { }

        public override bool Equals(object obj)
        {
            return obj is BackwardCompatible;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}