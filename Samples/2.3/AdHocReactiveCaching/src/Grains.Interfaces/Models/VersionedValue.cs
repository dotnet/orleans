using Orleans.Concurrency;

namespace Grains.Models
{
    [Immutable]
    public class VersionedValue<T>
    {
        public VersionedValue(int version, T value)
        {
            Version = version;
            Value = value;
        }

        public int Version { get; }
        public T Value { get; }

        public VersionedValue<T> NextVersion(T value)
        {
            unchecked
            {
                return new VersionedValue<T>(Version + 1, value);
            }
        }
    }
}
