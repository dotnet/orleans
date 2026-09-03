namespace Orleans.Runtime;

internal interface IDisseminationSystemTarget : ISystemTarget
{
    // The response carries receiver versions, which are the evidence used to sequence later repairs.
    Task<DisseminationBroadcastResponse> PushBroadcast(DisseminationBroadcastBatch batch, CancellationToken cancellationToken);

    Task<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(DisseminationAntiEntropyRequest request, CancellationToken cancellationToken);
}

[GenerateSerializer, Immutable]
internal readonly struct DisseminationNamespace : IEquatable<DisseminationNamespace>, IComparable<DisseminationNamespace>, IComparable, ISpanFormattable
{
    [Id(0)]
    private readonly string? _value;

    public DisseminationNamespace(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public string Value => _value ?? throw new InvalidOperationException($"A default {nameof(DisseminationNamespace)} value is invalid.");

    public static implicit operator DisseminationNamespace(string value) => new(value);

    public static implicit operator string(DisseminationNamespace value) => value.Value;

    public static bool operator ==(DisseminationNamespace left, DisseminationNamespace right) => left.Equals(right);

    public static bool operator !=(DisseminationNamespace left, DisseminationNamespace right) => !left.Equals(right);

    public bool Equals(DisseminationNamespace other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is DisseminationNamespace other && Equals(other);

    public override int GetHashCode() => _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    public int CompareTo(DisseminationNamespace other) => string.Compare(_value, other._value, StringComparison.Ordinal);

    public int CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        return obj is DisseminationNamespace other
            ? CompareTo(other)
            : throw new ArgumentException($"Object must be of type {nameof(DisseminationNamespace)}.", nameof(obj));
    }

    public override string ToString() => _value ?? string.Empty;

    public string ToString(string? format, IFormatProvider? formatProvider) => _value ?? string.Empty;

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? formatProvider)
    {
        var value = _value.AsSpan();
        if (value.TryCopyTo(destination))
        {
            charsWritten = value.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }
}

[GenerateSerializer, Immutable]
internal readonly struct DisseminationKey(object? value) : IEquatable<DisseminationKey>, IComparable<DisseminationKey>, IComparable, ISpanFormattable
{
    public static readonly DisseminationKey Default = default;

    [Id(0)]
    public readonly object? Value = value;

    public static implicit operator DisseminationKey(SiloAddress? value) => new(value);

    public static implicit operator DisseminationKey(string? value) => new(value);

    public static bool operator ==(DisseminationKey left, DisseminationKey right) => left.Equals(right);

    public static bool operator !=(DisseminationKey left, DisseminationKey right) => !left.Equals(right);

    public bool Equals(DisseminationKey other) => Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is DisseminationKey other && Equals(other);

    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    public int CompareTo(DisseminationKey other) => Compare(Value, other.Value);

    public int CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        return obj is DisseminationKey other
            ? CompareTo(other)
            : throw new ArgumentException($"Object must be of type {nameof(DisseminationKey)}.", nameof(obj));
    }

    public override string ToString() => ToString(null, null);

    public string ToString(string? format, IFormatProvider? formatProvider) => Value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(format, formatProvider),
        _ => Value.ToString() ?? string.Empty,
    };

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? formatProvider)
    {
        if (Value is null)
        {
            charsWritten = 0;
            return true;
        }

        if (Value is ISpanFormattable spanFormattable)
        {
            return spanFormattable.TryFormat(destination, out charsWritten, format, formatProvider);
        }

        var text = ToString(format.IsEmpty ? null : format.ToString(), formatProvider);
        if (text.AsSpan().TryCopyTo(destination))
        {
            charsWritten = text.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }

    private static int Compare(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        if (Equals(left, right))
        {
            return 0;
        }

        return left switch
        {
            string leftString when right is string rightString => StringComparer.Ordinal.Compare(leftString, rightString),
            SiloAddress leftSilo when right is SiloAddress rightSilo => leftSilo.CompareTo(rightSilo),
            _ => Comparer<object>.Default.Compare(left, right),
        };
    }
}

[GenerateSerializer, Immutable]
internal readonly struct DigestEntry
{
    public DigestEntry(DisseminationKey key, long version, long fingerprint = 0)
    {
        Key = key;
        Version = version;
        Fingerprint = fingerprint;
    }

    [Id(0)]
    public DisseminationKey Key { get; }

    [Id(1)]
    public long Version { get; }

    // Fingerprints distinguish meaningful same-version state, such as membership liveness.
    [Id(2)]
    public long Fingerprint { get; }
}

// FromVersion zero denotes a full value which can replace any baseline; nonzero ranges must form a chain.
[GenerateSerializer, Immutable]
internal readonly struct DisseminationValue
{
    public DisseminationValue(DisseminationKey key, long fromVersion, long toVersion, ReadOnlyMemory<byte> payload)
    {
        Key = key;
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Payload = payload;
    }

    [Id(0)]
    public DisseminationKey Key { get; }

    [Id(1)]
    public long FromVersion { get; }

    [Id(2)]
    public long ToVersion { get; }

    [Id(3)]
    public ReadOnlyMemory<byte> Payload { get; }
}

// Lifetime is hop-local so forwarding re-materializes both the payload and its delivery window.
[GenerateSerializer, Immutable]
internal sealed class DisseminationBroadcastValue
{
    [Id(0)]
    public DisseminationValue Value { get; init; }

    [Id(1)]
    public TimeSpan TimeToLive { get; init; }
}

// Sender is the immediate hop, not the original publisher.
[GenerateSerializer, Immutable]
internal sealed class DisseminationBroadcastBatch
{
    [Id(0)]
    public required SiloAddress Sender { get; init; }

    [Id(1)]
    public Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> Values { get; init; } = [];
}

// Acknowledgments report the versions the receiver actually holds after processing the batch.
[GenerateSerializer, Immutable]
internal sealed class DisseminationBroadcastResponse
{
    [Id(0)]
    public Dictionary<DisseminationNamespace, List<DigestEntry>> Acknowledgments { get; init; } = [];

    [Id(1)]
    public List<DisseminationNamespace> UnsupportedNamespaces { get; init; } = [];
}

[GenerateSerializer, Immutable]
internal sealed class DisseminationAntiEntropyRequest
{
    [Id(0)]
    public Dictionary<DisseminationNamespace, List<DigestEntry>> Digests { get; init; } = [];

    [Id(1)]
    public required SiloAddress Sender { get; init; }

    [Id(2)]
    public List<DisseminationNamespace> SupportedNamespaces { get; init; } = [];
}

[GenerateSerializer, Immutable]
internal sealed class DisseminationAntiEntropyResponse
{
    [Id(0)]
    public required SiloAddress Sender { get; init; }

    [Id(1)]
    public Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> Values { get; init; } = [];

    // Truncation means at least one valid repair remains for a later round.
    [Id(2)]
    public bool Truncated { get; init; }

    [Id(3)]
    public List<DisseminationNamespace> SupportedNamespaces { get; init; } = [];

    [Id(4)]
    public List<DisseminationNamespace> UnsupportedNamespaces { get; init; } = [];
}
