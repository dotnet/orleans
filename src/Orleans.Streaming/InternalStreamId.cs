using System;
using System.Runtime.Serialization;

#nullable enable
namespace Orleans.Runtime
{
    [Immutable]
    [Serializable]
    [GenerateSerializer]
    public readonly struct QualifiedStreamId : IEquatable<QualifiedStreamId>, IComparable<QualifiedStreamId>, ISerializable, ISpanFormattable
    {
        [Id(0)]
        public readonly StreamId StreamId;

        [Id(1)]
        public readonly string ProviderName;

        public QualifiedStreamId(string providerName, StreamId streamId)
        {
            ProviderName = providerName;
            StreamId = streamId;
        }

        private QualifiedStreamId(SerializationInfo info, StreamingContext context)
        {
            ProviderName = info.GetString("pvn")!;
            StreamId = (StreamId)info.GetValue("sid", typeof(StreamId))!;
        }

        public static implicit operator StreamId(QualifiedStreamId internalStreamId) => internalStreamId.StreamId;

        public bool Equals(QualifiedStreamId other) => StreamId.Equals(other) && ProviderName.Equals(other.ProviderName);

        public override bool Equals(object? obj) => obj is QualifiedStreamId other ? this.Equals(other) : false;

        public static bool operator ==(QualifiedStreamId s1, QualifiedStreamId s2) => s1.Equals(s2);

        public static bool operator !=(QualifiedStreamId s1, QualifiedStreamId s2) => !s2.Equals(s1);

        public int CompareTo(QualifiedStreamId other) => StreamId.CompareTo(other.StreamId);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("pvn", ProviderName);
            info.AddValue("sid", StreamId, typeof(StreamId));
        }

        public override int GetHashCode() => HashCode.Combine(ProviderName, StreamId);

        public override string ToString() => $"{ProviderName}/{StreamId}";
        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => destination.TryWrite($"{ProviderName}/{StreamId}", out charsWritten);

        internal string? GetNamespace() => StreamId.GetNamespace();
    }
}