using System;

namespace Orleans.Persistence.EntityFrameworkCore.Data;

public class GrainStateRecord
{
    public string ServiceId { get; set; } = default!;
    public string GrainType { get; set; } = default!;
    public string StateType { get; set; } = default!;
    public string GrainId { get; set; } = default!;
    public string? Data { get; set; }
    public byte[] ETag { get; set; } = Array.Empty<byte>();
}