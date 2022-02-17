using System;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    [GenerateSerializer]
    internal readonly struct InternalStreamId : IEquatable<InternalStreamId>, IComparable<InternalStreamId>, ISerializable
    {
        [Id(0)]
        public StreamId StreamId { get; }

        [Id(1)]
        public string ProviderName { get; }

        public InternalStreamId(string providerName, StreamId streamId)
        {
            ProviderName = providerName;
            StreamId = streamId;
        }

        private InternalStreamId(SerializationInfo info, StreamingContext context)
        {
            ProviderName = info.GetString("pvn");
            StreamId = (StreamId) info.GetValue("sid", typeof(StreamId));
        }

        public static implicit operator StreamId(InternalStreamId internalStreamId) => internalStreamId.StreamId;

        public bool Equals(InternalStreamId other) => StreamId.Equals(other) && ProviderName.Equals(other.ProviderName);

        public override bool Equals(object obj) => obj is InternalStreamId other ? this.Equals(other) : false;

        public static bool operator ==(InternalStreamId s1, InternalStreamId s2) => s1.Equals(s2);

        public static bool operator !=(InternalStreamId s1, InternalStreamId s2) => !s2.Equals(s1);

        public int CompareTo(InternalStreamId other) => StreamId.CompareTo(other.StreamId);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("pvn", ProviderName);
            info.AddValue("sid", StreamId, typeof(StreamId));
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ProviderName.GetHashCode() * 43 ^ StreamId.GetHashCode();
            }
        }

        public override string ToString()
        {
            return $"{ProviderName}/{StreamId.ToString()}";
        }

        public static InternalStreamId Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                ThrowInvalidInternalStreamId(value);
            }

            var i = value.IndexOf('/');
            if (i < 0)
            {
                ThrowInvalidInternalStreamId(value);
            }

            return new InternalStreamId(value.Substring(0, i), StreamId.Parse(value.Substring(i + 1)));
        }

        private static void ThrowInvalidInternalStreamId(string value) => throw new ArgumentException($"Unable to parse \"{value}\" as a stream id");


        internal string GetNamespace() => StreamId.GetNamespace();
    }
}