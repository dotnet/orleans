using System;
using System.Collections.Generic;

namespace Orleans.Clustering.EntityFrameworkCore.Data;

public class ClusterRecord
{
    public string Id { get; set; } = default!;
    public DateTimeOffset Timestamp { get; set; }
    public int Version { get; set; }
    public byte[] ETag { get; set; } = Array.Empty<byte>();
    public List<SiloRecord> Silos { get; set; } = new();
}