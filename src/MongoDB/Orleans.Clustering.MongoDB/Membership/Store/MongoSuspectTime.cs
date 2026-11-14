using System;
using MongoDB.Bson.Serialization.Attributes;
using Orleans.Runtime;

namespace Orleans.Providers.MongoDB.Membership.Store;

internal sealed class MongoSuspectTime
{
    [BsonRequired]
    public string Address { get; set; } = null!;

    [BsonRequired]
    public string IAmAliveTime { get; set; } = null!;

    public static MongoSuspectTime Create(Tuple<SiloAddress, DateTime> tuple)
    {
        return new MongoSuspectTime { Address = tuple.Item1.ToParsableString(), IAmAliveTime = LogFormatter.PrintDate(tuple.Item2) };
    }

    public Tuple<SiloAddress, DateTime> ToTuple()
    {
        return Tuple.Create(SiloAddress.FromParsableString(Address), LogFormatter.ParseDate(IAmAliveTime));
    }
}
