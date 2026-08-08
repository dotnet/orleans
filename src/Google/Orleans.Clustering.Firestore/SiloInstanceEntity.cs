using System;
using System.Net;
using System.Collections.Generic;
using Google.Cloud.Firestore;
using Orleans.Runtime;

namespace Orleans.Clustering.Firestore;

[FirestoreData]
internal class SiloInstanceEntity : FirestoreEntity
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
    public int Status { get; set; }

    [FirestoreProperty("ProxyPort")]
    public int ProxyPort { get; set; }

    [FirestoreProperty("SiloName")]
    public string SiloName { get; set; } = default!;

    [FirestoreProperty("RoleName")]
    public string? RoleName { get; set; }

    [FirestoreProperty("UpdateZone")]
    public int UpdateZone { get; set; }

    [FirestoreProperty("FaultZone")]
    public int FaultZone { get; set; }

    [FirestoreProperty("SuspectingSilos")]
    public string[]? SuspectingSilos { get; set; }

    [FirestoreProperty("SuspectingTimes")]
    public DateTimeOffset[]? SuspectingTimes { get; set; }

    [FirestoreProperty("MembershipVersion")]
    public int MembershipVersion { get; set; }

    [FirestoreProperty("StartTime")]
    public DateTimeOffset StartTime { get; set; } = default!;

    [FirestoreProperty("IAmAliveTime")]
    public DateTimeOffset IAmAliveTime { get; set; } = default!;

    public override IDictionary<string, object?> GetFields()
    {
        return new Dictionary<string, object?>
        {
            ["Address"] = this.Address,
            ["Port"] = this.Port,
            ["Generation"] = this.Generation,
            ["HostName"] = this.HostName,
            ["Status"] = this.Status,
            ["ProxyPort"] = this.ProxyPort,
            ["SiloName"] = this.SiloName,
            ["RoleName"] = this.RoleName,
            ["UpdateZone"] = this.UpdateZone,
            ["FaultZone"] = this.FaultZone,
            ["SuspectingSilos"] = this.SuspectingSilos,
            ["SuspectingTimes"] = this.SuspectingTimes,
            ["MembershipVersion"] = this.MembershipVersion,
            ["StartTime"] = this.StartTime,
            ["IAmAliveTime"] = this.IAmAliveTime,
        };
    }

    public MembershipEntry ToMembershipEntry()
    {
        var entry = new MembershipEntry
        {
            HostName = this.HostName,
            Status = (SiloStatus)this.Status,
            ProxyPort = this.ProxyPort,
            SiloAddress = SiloAddress.New(IPAddress.Parse(this.Address), this.Port, this.Generation),
            SiloName = this.SiloName,
            RoleName = this.RoleName,
            UpdateZone = this.UpdateZone,
            FaultZone = this.FaultZone,
            StartTime = this.StartTime.UtcDateTime,
            IAmAliveTime = this.IAmAliveTime.UtcDateTime,
        };

        if (this.SuspectingSilos is not null || this.SuspectingTimes is not null)
        {
            if (this.SuspectingSilos is null
                || this.SuspectingTimes is null
                || this.SuspectingSilos.Length != this.SuspectingTimes.Length)
            {
                throw new OrleansException("The stored suspecting silo and timestamp lists have different lengths.");
            }

            for (var i = 0; i < this.SuspectingSilos.Length; i++)
            {
                entry.AddSuspector(
                    SiloAddress.FromParsableString(this.SuspectingSilos[i]),
                    this.SuspectingTimes[i].UtcDateTime);
            }
        }

        return entry;
    }

    public static SiloInstanceEntity FromMembershipEntry(MembershipEntry entry, int membershipVersion)
    {
        var siloInstance = new SiloInstanceEntity
        {
            Id = entry.SiloAddress.ToParsableString(),
            Address = entry.SiloAddress.Endpoint.Address.ToString(),
            Port = entry.SiloAddress.Endpoint.Port,
            Generation = entry.SiloAddress.Generation,
            HostName = entry.HostName,
            Status = (int)entry.Status,
            ProxyPort = entry.ProxyPort,
            SiloName = entry.SiloName,
            RoleName = entry.RoleName,
            UpdateZone = entry.UpdateZone,
            FaultZone = entry.FaultZone,
            MembershipVersion = membershipVersion,
            StartTime = DateTime.SpecifyKind(entry.StartTime, DateTimeKind.Utc),
            IAmAliveTime = DateTime.SpecifyKind(entry.IAmAliveTime, DateTimeKind.Utc),
        };

        if (entry.SuspectTimes is not null)
        {
            siloInstance.SuspectingSilos = new string[entry.SuspectTimes.Count];
            siloInstance.SuspectingTimes = new DateTimeOffset[entry.SuspectTimes.Count];
            for (var i = 0; i < entry.SuspectTimes.Count; i++)
            {
                var suspect = entry.SuspectTimes[i];
                siloInstance.SuspectingSilos[i] = suspect.Item1.ToParsableString();
                siloInstance.SuspectingTimes[i] = DateTime.SpecifyKind(suspect.Item2, DateTimeKind.Utc);
            }
        }

        return siloInstance;
    }
}