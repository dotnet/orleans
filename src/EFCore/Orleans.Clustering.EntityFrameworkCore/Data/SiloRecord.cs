using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Clustering.EntityFrameworkCore.Data;

public class SiloRecord
{
    public string ClusterId { get; set; } = default!;
    public string Address { get; set; } = default!;
    public int Port { get; set; }
    public int Generation { get; set; }
    public string Name { get; set; } = default!;
    public string HostName { get; set; } = default!;
    public SiloStatus Status { get; set; }
    public int? ProxyPort { get; set; }
    public List<string> SuspectingTimes { get; set; } = new();
    public List<string> SuspectingSilos { get; set; } = new();
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset IAmAliveTime { get; set; }
    public byte[] ETag { get; set; } = Array.Empty<byte>();
    public ClusterRecord Cluster { get; set; } = default!;
}