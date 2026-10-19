using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Messaging.Tests.Contracts;

/// <summary>
/// Pins the serialized bytes of every messaging wire type. Messages cross silos of different versions and persist in
/// journals, so any change to these bytes is a compatibility decision which must update the golden values deliberately.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class WireFormatTests : IDisposable
{
    private static readonly GrainId Receiver = GrainId.Create("receiver", "r1");
    private static readonly MessageId Id = MessageId.Create("orders", "4/2", "reserve");
    private static readonly DateTimeOffset DeliverAt = new(2026, 10, 19, 12, 30, 0, TimeSpan.FromHours(2));
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();

    public static TheoryData<string, string> Golden => new()
    {
        { "MessageId", "2040276F72646572732F345C2F322F72657365727665E0" },
        { "HierarchicalKey", "2040276F72646572732F345C2F322F72657365727665E0" },
        { "Envelope.Minimal", "202040276F72646572732F345C2F322F72657365727665E04101C101C101C101E0" },
        { "Envelope.Full", "202040276F72646572732F345C2F322F72657365727665E041070102034129696E76656E746F72792E726573657276652E7631210103410B747261636540050908E02180005444B1DC2DDF088100D088C310000000E0E0" },
        { "InboxMessage", "20202040276F72646572732F345C2F322F72657365727665E041070102034129696E76656E746F72792E726573657276652E7631210103410B747261636540050908E02180005444B1DC2DDF088100D088C310000000E0E0E0" },
        { "OutboxMessage", "20202040276F72646572732F345C2F322F72657365727665E041070102034129696E76656E746F72792E726573657276652E7631210103410B747261636540050908E02180005444B1DC2DDF088100D088C310000000E0E021202060697CAED241117265636569766572E0E021605D5ABF1541057231E0E0E0" },
        { "DeliveryResult.Accepted", "20080305C101C101E0" },
        { "DeliveryResult.Duplicate", "20080309C101C101E0" },
        { "DeliveryResult.Backpressured", "2008030DC101C101E0" },
        { "DeliveryResult.HandlerNotFound", "20080311413F4E6F20696E626F782068616E646C657220697320726567697374657265642EC101E0" },
        { "DeliveryResult.DeadLettered", "2008031541196F7574206F662073746F636B41196F72646572732F73746F636BE0" },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void Encoding_MatchesGoldenBytesAndDecodesFromThem(string name, string hex)
    {
        switch (name)
        {
            case "MessageId": Check(Id, hex); break;
            case "HierarchicalKey": Check(Id.Key, hex); break;
            case "Envelope.Minimal": Check(new Envelope { MessageId = Id, Payload = [] }, hex, AssertEnvelope); break;
            case "Envelope.Full": Check(FullEnvelope(), hex, AssertEnvelope); break;
            case "InboxMessage": Check(new InboxMessage { Envelope = FullEnvelope() }, hex, (e, a) => AssertEnvelope(e.Envelope, a.Envelope)); break;
            case "OutboxMessage":
                Check(new OutboxMessage { Envelope = FullEnvelope(), ReceiverId = Receiver }, hex, (e, a) =>
                {
                    AssertEnvelope(e.Envelope, a.Envelope);
                    Assert.Equal(e.ReceiverId, a.ReceiverId);
                });
                break;
            case "DeliveryResult.Accepted": Check(DeliveryResult.Accepted(), hex); break;
            case "DeliveryResult.Duplicate": Check(DeliveryResult.Duplicate(), hex); break;
            case "DeliveryResult.Backpressured": Check(DeliveryResult.Backpressured(), hex); break;
            case "DeliveryResult.HandlerNotFound": Check(DeliveryResult.HandlerNotFound(), hex); break;
            case "DeliveryResult.DeadLettered": Check(DeliveryResult.DeadLettered("out of stock", "orders/stock"), hex); break;
            default: throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }

    public void Dispose() => _services.Dispose();

    private static Envelope FullEnvelope() => new()
    {
        MessageId = Id,
        Payload = [1, 2, 3],
        Subject = "inventory.reserve.v1",
        Headers = new() { ["trace"] = [9, 8] },
        DeliverAt = DeliverAt
    };

    private void Check<T>(T value, string hex, Action<T, T>? assertEqual = null)
    {
        var serializer = _services.GetRequiredService<Serializer<T>>();
        var actual = Convert.ToHexString(serializer.SerializeToArray(value));
        Assert.Equal(hex, actual);
        var decoded = serializer.Deserialize(Convert.FromHexString(hex));
        if (assertEqual is null) Assert.Equal(value, decoded);
        else assertEqual(value, decoded!);
    }

    private static void AssertEnvelope(Envelope expected, Envelope actual)
    {
        Assert.Equal(expected.MessageId, actual.MessageId);
        Assert.Equal(expected.Payload, actual.Payload);
        Assert.Equal(expected.Subject, actual.Subject);
        Assert.Equal(expected.DeliverAt, actual.DeliverAt);
        Assert.Equal(expected.DeliverAt?.Offset, actual.DeliverAt?.Offset);
        Assert.Equal(expected.Headers?.OrderBy(static h => h.Key, StringComparer.Ordinal).Select(static h => (h.Key, Convert.ToHexString(h.Value))),
            actual.Headers?.OrderBy(static h => h.Key, StringComparer.Ordinal).Select(static h => (h.Key, Convert.ToHexString(h.Value))));
    }
}