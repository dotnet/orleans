using System;

namespace Orleans.GrainDirectory.EntityFrameworkCore.Data;

public class GrainActivationRecord
{
    public string ClusterId { get; set; } = default!;
    public string GrainId { get; set; } = default!;
    public string SiloAddress { get; set; } = default!;
    public string ActivationId { get; set; } = default!;
    public long MembershipVersion { get; set; }
    public byte[] ETag { get; set; } = Array.Empty<byte>();
}