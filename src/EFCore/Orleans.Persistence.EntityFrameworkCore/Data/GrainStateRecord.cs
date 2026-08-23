using Orleans.EntityFrameworkCore;

namespace Orleans.Persistence.EntityFrameworkCore.Data;

public class GrainStateRecord<TETag>
{
    private byte[]? _keyHash;

    internal byte[] KeyHash
    {
        get => _keyHash ??= EFCoreIdentifierHash.Compute(ServiceId, GrainType, StateType, GrainId);
        set => _keyHash = value;
    }

    public string ServiceId { get; set; } = default!;
    public string GrainType { get; set; } = default!;
    public string StateType { get; set; } = default!;
    public string GrainId { get; set; } = default!;
    public byte[]? Data { get; set; }
    public TETag ETag { get; set; } = default!;
}