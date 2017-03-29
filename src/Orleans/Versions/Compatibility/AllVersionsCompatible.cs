namespace Orleans.Versions.Compatibility
{
    public class AllVersionsCompatible : VersionCompatibilityStrategy
    {
        internal static AllVersionsCompatible Singleton { get; } = new AllVersionsCompatible();

        private AllVersionsCompatible()
        { }

        public override bool Equals(object obj)
        {
            return obj is AllVersionsCompatible;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}
