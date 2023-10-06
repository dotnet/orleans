namespace Orleans.GrainDirectory.EntityFrameworkCore.Data;

public class GrainActivationRecord<TETag>
{
    public string ClusterId { get; set; } = default!;
    public string GrainId { get; set; } = default!;
    public string SiloAddress { get; set; } = default!;
    public string ActivationId { get; set; } = default!;
    public long MembershipVersion { get; set; }
    public TETag ETag { get; set; } = default!;
}