using Orleans.Concurrency;

namespace Grains.Models
{
    [Immutable]
    public class VersionedValue<T>
    {
        public VersionedValue(VersionToken version, T value)
        {
            Version = version;
            Value = value;
        }

        public VersionToken Version { get; }
        public T Value { get; }

        /// <summary>
        /// True if the current version is different from <see cref="VersionToken.None"/>, otherwise false.
        /// </summary>
        public bool IsValid => Version != VersionToken.None;

        public VersionedValue<T> NextVersion(T value) => new VersionedValue<T>(Version.Next(), value);

        public static VersionedValue<T> None { get; } = new VersionedValue<T>(VersionToken.None, default);
    }
}
