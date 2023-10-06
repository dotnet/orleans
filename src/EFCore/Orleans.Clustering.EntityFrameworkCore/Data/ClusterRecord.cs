using System;
using System.Collections.Generic;

namespace Orleans.Clustering.EntityFrameworkCore.Data;

public class ClusterRecord<TETag>
{
    public string Id { get; set; } = default!;
    public DateTimeOffset Timestamp { get; set; }
    public int Version { get; set; }
    public TETag ETag { get; set; } = default!;
    public List<SiloRecord<TETag>> Silos { get; set; } = new();
}