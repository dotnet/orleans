using Orleans.EntityFrameworkCore;

namespace Orleans.GrainDirectory.EntityFrameworkCore.Data;

public class GrainActivationRecord<TETag>
{
    private byte[]? _clusterIdHash;
    private byte[]? _grainIdHash;
    private byte[]? _siloAddressHash;

    internal byte[] ClusterIdHash
    {
        get => _clusterIdHash ??= EFCoreIdentifierHash.Compute(ClusterId);
        set => _clusterIdHash = value;
    }

    internal byte[] GrainIdHash
    {
        get => _grainIdHash ??= EFCoreIdentifierHash.Compute(GrainId);
        set => _grainIdHash = value;
    }

    internal byte[] SiloAddressHash
    {
        get => _siloAddressHash ??= EFCoreIdentifierHash.Compute(SiloAddress);
        set => _siloAddressHash = value;
    }

    public string ClusterId { get; set; } = default!;
    public string GrainId { get; set; } = default!;
    public string SiloAddress { get; set; } = default!;
    public string ActivationId { get; set; } = default!;
    public long MembershipVersion { get; set; }
    public TETag ETag { get; set; } = default!;
}
