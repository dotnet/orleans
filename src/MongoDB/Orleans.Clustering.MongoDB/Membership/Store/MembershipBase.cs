using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Orleans.Runtime;
using SiloAddressClass = Orleans.Runtime.SiloAddress;

namespace Orleans.Providers.MongoDB.Membership.Store;

internal class MembershipBase
{
    [BsonRequired]
    public string Etag { get; set; } = null!;

    [BsonRequired]
    public string HostName { get; set; } = null!;

    [BsonRequired]
    public string SiloAddress { get; set; } = null!;

    [BsonRequired]
    public string SiloName { get; set; } = null!;

    [BsonRequired]
    public string? RoleName { get; set; }

    [BsonIgnoreIfNull]
    public string StatusText { get; set; } = null!;

    [BsonRequired]
    public string IAmAliveTime { get; set; } = null!;

    [BsonRequired]
    public string StartTime { get; set; } = null!;

    [BsonRequired]
    public int ProxyPort { get; set; }

    [BsonRequired]
    public int UpdateZone { get; set; }

    [BsonRequired]
    public int FaultZone { get; set; }

    [BsonRequired]
    public int Status { get; set; }

    [BsonRequired]
    public List<MongoSuspectTime> SuspectTimes { get; set; } = null!;

    [BsonIgnoreIfDefault]
    public DateTime Timestamp { get; set; }

    internal bool IsDefunctBefore(DateTime beforeUtc)
        => Status == (int)SiloStatus.Dead
            && LogFormatter.ParseDate(StartTime) < beforeUtc
            && LogFormatter.ParseDate(IAmAliveTime) < beforeUtc
            && SuspectTimes.All(vote => LogFormatter.ParseDate(vote.IAmAliveTime) < beforeUtc);

    internal static FilterDefinition<T> DefunctBefore<T>(DateTime beforeUtc, string prefix = "")
    {
        var filter = Builders<T>.Filter;
        var cutoff = LogFormatter.PrintDate(beforeUtc);
        // Persisted membership times have millisecond precision. A fractional cutoff includes that millisecond.
        var includeCutoff = beforeUtc.Ticks % TimeSpan.TicksPerMillisecond != 0;
        var start = includeCutoff ? filter.Lte($"{prefix}StartTime", cutoff) : filter.Lt($"{prefix}StartTime", cutoff);
        var alive = includeCutoff ? filter.Lte($"{prefix}IAmAliveTime", cutoff) : filter.Lt($"{prefix}IAmAliveTime", cutoff);
        var voteFilter = Builders<MongoSuspectTime>.Filter;
        var recentVote = includeCutoff
            ? voteFilter.Gt(vote => vote.IAmAliveTime, cutoff)
            : voteFilter.Gte(vote => vote.IAmAliveTime, cutoff);
        return filter.And(
            filter.Eq($"{prefix}Status", (int)SiloStatus.Dead),
            start,
            alive,
            filter.Not(filter.ElemMatch($"{prefix}SuspectTimes", recentVote)));
    }

    public static T Create<T>(MembershipEntry entry) where T : MembershipBase, new()
    {
        var suspectTimes =
            entry.SuspectTimes?.Select(MongoSuspectTime.Create).ToList() ?? new List<MongoSuspectTime>();

        return new T
        {
            Etag = EtagHelper.CreateNew(),
            FaultZone = entry.FaultZone,
            HostName = entry.HostName,
            IAmAliveTime = LogFormatter.PrintDate(entry.IAmAliveTime),
            ProxyPort = entry.ProxyPort,
            RoleName = entry.RoleName,
            SiloAddress = entry.SiloAddress.ToParsableString(),
            SiloName = entry.SiloName,
            Status = (int)entry.Status,
            StatusText = entry.Status.ToString(),
            StartTime = LogFormatter.PrintDate(entry.StartTime),
            SuspectTimes = suspectTimes,
            Timestamp = entry.IAmAliveTime,
            UpdateZone = entry.UpdateZone
        };
    }

    public MembershipEntry ToEntry()
    {
        return new MembershipEntry
        {
            FaultZone = FaultZone,
            HostName = HostName,
            IAmAliveTime = LogFormatter.ParseDate(IAmAliveTime),
            ProxyPort = ProxyPort,
            RoleName = RoleName,
            SiloAddress = SiloAddressClass.FromParsableString(SiloAddress),
            SiloName = SiloName,
            Status = (SiloStatus)Status,
            StartTime = LogFormatter.ParseDate(StartTime),
            SuspectTimes = SuspectTimes.Select(x => x.ToTuple()).ToList(),
            UpdateZone = UpdateZone
        };
    }

    public Uri ToGatewayUri()
    {
        var siloAddress = SiloAddressClass.FromParsableString(SiloAddress);

        return SiloAddressClass.New(new IPEndPoint(siloAddress.Endpoint.Address, ProxyPort), siloAddress.Generation).ToGatewayUri();
    }
}
