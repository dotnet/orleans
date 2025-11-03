using Orleans.Runtime;

namespace Orleans.Dashboard.Model;

[GenerateSerializer]
internal class SiloDetails
{
    [Id(0)]
    public int FaultZone { get; set; }

    [Id(1)]
    public string HostName { get; set; }

    [Id(2)]
    public string IAmAliveTime { get; set; }

    [Id(3)]
    public int ProxyPort { get; set; }

    [Id(4)]
    public string RoleName { get; set; }

    [Id(5)]
    public string SiloAddress { get; set; }

    [Id(6)]
    public string SiloName { get; set; }

    [Id(7)]
    public string StartTime { get; set; }

    [Id(8)]
    public string Status { get; set; }

    [Id(9)]
    public int UpdateZone { get; set; }

    [Id(10)]
    public SiloStatus SiloStatus { get; set; }
}
