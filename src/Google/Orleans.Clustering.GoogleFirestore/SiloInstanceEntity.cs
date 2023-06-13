using System;
using System.Net;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using Google.Cloud.Firestore;
using Orleans.Runtime;

namespace Orleans.Clustering.GoogleFirestore;

[FirestoreData]
internal class SiloInstanceEntity : MembershipEntity
{
    [FirestoreProperty("Address")]
    public string Address { get; set; } = default!;

    [FirestoreProperty("Port")]
    public int Port { get; set; } = default!;

    [FirestoreProperty("Generation")]
    public int Generation { get; set; }

    [FirestoreProperty("HostName")]
    public string HostName { get; set; } = default!;

    [FirestoreProperty("Status")]
    public string Status { get; set; } = default!;

    [FirestoreProperty("ProxyPort")]
    public int ProxyPort { get; set; }

    [FirestoreProperty("SiloName")]
    public string SiloName { get; set; } = default!;

    [FirestoreProperty("SuspectingSilos")]
    public Dictionary<string, DateTimeOffset>? SuspectingSilos { get; set; }

    [FirestoreProperty("StartTime")]
    public DateTimeOffset StartTime { get; set; } = default!;

    [FirestoreProperty("IAmAliveTime")]
    public DateTimeOffset IAmAliveTime { get; set; } = default!;

    public override IDictionary<string, object?> GetFields()
    {
        var fields = base.GetFields();
        fields.Add("Address", this.Address);
        fields.Add("Port", this.Port);
        fields.Add("Generation", this.Generation);
        fields.Add("HostName", this.HostName);
        fields.Add("Status", this.Status);
        fields.Add("ProxyPort", this.ProxyPort);
        fields.Add("SiloName", this.SiloName);
        fields.Add("SuspectingSilos", this.SuspectingSilos);
        fields.Add("StartTime", this.StartTime);
        fields.Add("IAmAliveTime", this.IAmAliveTime);
        return fields;
    }

    public IDictionary<string, object?> GetIAmAliveFields()
    {
        var fields = base.GetFields();
        fields.Add("IAmAliveTime", this.IAmAliveTime);
        return fields;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();

        sb.Append("OrleansSilo [");
        sb.Append(" Deployment=").Append(this.ClusterId);
        sb.Append(" LocalEndpoint=").Append(this.Address);
        sb.Append(" LocalPort=").Append(this.Port);
        sb.Append(" Generation=").Append(this.Generation);
        sb.Append(" Host=").Append(this.HostName);
        sb.Append(" Status=").Append(this.Status);
        sb.Append(" ProxyPort=").Append(this.ProxyPort);
        sb.Append(" SiloName=").Append(this.SiloName);

        sb.Append(" SuspectingSilos=")
            .Append(string.Join("|", this.SuspectingSilos?.Select(s => $"({s.Key}|{Utils.FormatDateTime(s.Value)}") ?? Enumerable.Empty<string>()));

        sb.Append(" StartTime=").Append(Utils.FormatDateTime(this.StartTime));
        sb.Append(" IAmAliveTime=").Append(Utils.FormatDateTime(this.IAmAliveTime));
        sb.Append(']');

        return sb.ToString();
    }

    public MembershipEntry ToMembershipEntry()
    {
        var entry = new MembershipEntry
        {
            HostName = this.HostName,
            Status = (SiloStatus)Enum.Parse(typeof(SiloStatus), this.Status),
            ProxyPort = this.ProxyPort,
            SiloAddress = SiloAddress.New(IPAddress.Parse(this.Address), this.Port, this.Generation),
            SiloName = this.SiloName,
            StartTime = this.StartTime.UtcDateTime,
            IAmAliveTime = this.IAmAliveTime.UtcDateTime,
        };

        if (this.SuspectingSilos is not null)
        {
            foreach (var silo in this.SuspectingSilos.OrderBy(t => t.Value))
            {
                entry.AddSuspector(SiloAddress.FromParsableString(silo.Key), silo.Value.UtcDateTime);
            }
        }

        return entry;
    }

    public static SiloInstanceEntity FromMembershipEntry(MembershipEntry entry, string clusterId)
    {
        var siloInstance = new SiloInstanceEntity
        {
            Id = entry.SiloAddress.ToParsableString(),
            ClusterId = clusterId,
            Address = entry.SiloAddress.Endpoint.Address.ToString(),
            Port = entry.SiloAddress.Endpoint.Port,
            Generation = entry.SiloAddress.Generation,
            HostName = entry.HostName,
            Status = entry.Status.ToString(),
            ProxyPort = entry.ProxyPort,
            SiloName = entry.SiloName,
            StartTime = entry.StartTime,
            IAmAliveTime = entry.IAmAliveTime,
        };

        if (entry.SuspectTimes is not null)
        {
            siloInstance.SuspectingSilos = new Dictionary<string, DateTimeOffset>();
            foreach (var silo in entry.SuspectTimes.OrderBy(t => t.Item2))
            {
                siloInstance.SuspectingSilos.Add(silo.Item1.ToParsableString(), silo.Item2);
            }
        }

        return siloInstance;
    }
}