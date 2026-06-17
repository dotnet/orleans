using System;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime;

internal interface IDisseminationSystemTarget : ISystemTarget
{
    Task<DisseminationCapabilityResponse> GetCapabilities(DisseminationCapabilityRequest request);

    [OneWay]
    Task PushGossip(DisseminationGossipBatch batch);

    Task<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(DisseminationAntiEntropyRequest request);
}

[GenerateSerializer]
internal sealed class DisseminationCapabilityRequest
{
    [Id(0)]
    public string Topic { get; init; } = string.Empty;

    [Id(1)]
    public int ProtocolVersion { get; init; }

    [Id(2)]
    public string[] PayloadKinds { get; init; } = Array.Empty<string>();
}

[GenerateSerializer]
internal sealed class DisseminationCapabilityResponse
{
    [Id(0)]
    public string Topic { get; init; } = string.Empty;

    [Id(1)]
    public int ProtocolVersion { get; init; }

    [Id(2)]
    public bool Supported { get; init; }

    [Id(3)]
    public string[] PayloadKinds { get; init; } = Array.Empty<string>();
}

[GenerateSerializer, Immutable]
internal readonly struct DisseminationValueKey : IEquatable<DisseminationValueKey>
{
    public DisseminationValueKey(string value, SiloAddress? siloAddress = null)
    {
        Value = value ?? string.Empty;
        SiloAddress = siloAddress;
    }

    [Id(0)]
    public string Value { get; }

    [Id(1)]
    public SiloAddress? SiloAddress { get; }

    public static DisseminationValueKey FromString(string value) => new(value);

    public static DisseminationValueKey FromSiloAddress(SiloAddress siloAddress) =>
        new(siloAddress?.ToParsableString() ?? string.Empty, siloAddress);

    public bool Equals(DisseminationValueKey other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal)
        && Equals(SiloAddress, other.SiloAddress);

    public override bool Equals(object? obj) => obj is DisseminationValueKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(Value ?? string.Empty),
        SiloAddress);

    public override string ToString() => SiloAddress?.ToParsableString() ?? Value ?? string.Empty;

    public static bool operator ==(DisseminationValueKey left, DisseminationValueKey right) => left.Equals(right);

    public static bool operator !=(DisseminationValueKey left, DisseminationValueKey right) => !left.Equals(right);
}

[GenerateSerializer, Immutable]
internal readonly struct DisseminationItemId : IEquatable<DisseminationItemId>
{
    public DisseminationItemId(string topic, DisseminationValueKey key, long version, string payloadKind)
    {
        Topic = topic;
        Key = key;
        Version = version;
        PayloadKind = payloadKind;
    }

    [Id(0)]
    public string Topic { get; }

    [Id(1)]
    public DisseminationValueKey Key { get; }

    [Id(2)]
    public long Version { get; }

    [Id(3)]
    public string PayloadKind { get; }

    public bool Equals(DisseminationItemId other) =>
        string.Equals(Topic, other.Topic, StringComparison.Ordinal)
        && Key.Equals(other.Key)
        && Version == other.Version
        && string.Equals(PayloadKind, other.PayloadKind, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is DisseminationItemId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(Topic ?? string.Empty),
        Key,
        Version,
        StringComparer.Ordinal.GetHashCode(PayloadKind ?? string.Empty));

    public override string ToString() => $"{Topic}/{Key}/{Version}/{PayloadKind}";

    public static bool operator ==(DisseminationItemId left, DisseminationItemId right) => left.Equals(right);

    public static bool operator !=(DisseminationItemId left, DisseminationItemId right) => !left.Equals(right);
}

[GenerateSerializer]
internal sealed class DisseminationItem
{
    [Id(0)]
    public DisseminationItemId Id { get; init; }

    [Id(1)]
    public SiloAddress Root { get; init; } = default!;

    [Id(2)]
    public DateTimeOffset ExpiresAt { get; init; }

    [Id(3)]
    public byte[] Payload { get; init; } = Array.Empty<byte>();
}

[GenerateSerializer]
internal sealed class DisseminationGossipBatch
{
    [Id(0)]
    public SiloAddress Sender { get; init; } = default!;

    [Id(1)]
    public DisseminationItem[] Items { get; init; } = Array.Empty<DisseminationItem>();
}

[GenerateSerializer]
internal sealed class DisseminationAntiEntropyRequest
{
    [Id(0)]
    public SiloAddress Sender { get; init; } = default!;

    [Id(1)]
    public DisseminationCapabilityRequest[] Topics { get; init; } = Array.Empty<DisseminationCapabilityRequest>();

    [Id(2)]
    public DisseminationItemId[] Digests { get; init; } = Array.Empty<DisseminationItemId>();
}

[GenerateSerializer]
internal sealed class DisseminationAntiEntropyResponse
{
    [Id(0)]
    public SiloAddress Sender { get; init; } = default!;

    [Id(1)]
    public DisseminationItem[] Items { get; init; } = Array.Empty<DisseminationItem>();

    [Id(2)]
    public bool Truncated { get; init; }
}
