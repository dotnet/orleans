using System;

namespace Orleans.Versions.Compatibility
{
    [Serializable]
    public class StrictVersionCompatible : CompatibilityStrategy
    {
        public static StrictVersionCompatible Singleton { get; } = new StrictVersionCompatible();

        private StrictVersionCompatible()
        { }

        public override bool Equals(object obj)
        {
            return obj is StrictVersionCompatible;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}