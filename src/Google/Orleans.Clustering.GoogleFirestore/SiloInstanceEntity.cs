using System;
using System.Text;
using System.Collections.Generic;
using Google.Cloud.Firestore;

namespace Orleans.Clustering.GoogleFirestore;

[FirestoreData]
internal class SiloInstanceEntity : MembershipEntity
{
    [FirestoreProperty("Address")]
    public string Address { get; set; } = default!;

    [FirestoreProperty("Port")]
    public string Port { get; set; } = default!;

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
    public string? SuspectingSilos { get; set; }

    [FirestoreProperty("SuspectingTimes")]
    public string? SuspectingTimes { get; set; }

    [FirestoreProperty("StartTime")]
    public DateTimeOffset StartTime { get; set; } = default!;

    [FirestoreProperty("IAmAliveTime")]
    public string IAmAliveTime { get; set; } = default!;

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
        fields.Add("SuspectingTimes", this.SuspectingTimes);
        fields.Add("StartTime", this.StartTime);
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

        if (!string.IsNullOrEmpty(this.SuspectingSilos)) sb.Append(" SuspectingSilos=").Append(this.SuspectingSilos);
        if (!string.IsNullOrEmpty(this.SuspectingTimes)) sb.Append(" SuspectingTimes=").Append(this.SuspectingTimes);
        sb.Append(" StartTime=").Append(this.StartTime);
        sb.Append(" IAmAliveTime=").Append(this.IAmAliveTime);
        sb.Append(']');

        return sb.ToString();
    }
}