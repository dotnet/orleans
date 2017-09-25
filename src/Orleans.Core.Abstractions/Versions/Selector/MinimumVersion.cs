using System;

namespace Orleans.Versions.Selector
{
    [Serializable]
    public class MinimumVersion : VersionSelectorStrategy
    {
        public static MinimumVersion Singleton { get; } = new MinimumVersion();

        private MinimumVersion()
        { }

        public override bool Equals(object obj)
        {
            return obj is MinimumVersion;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}