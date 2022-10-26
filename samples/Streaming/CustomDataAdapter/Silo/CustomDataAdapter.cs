using System.Text.Json;
using Azure.Messaging.EventHubs;
using Newtonsoft.Json.Serialization;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;

namespace Silo;

// Custom EventHubDataAdapter that serialize event using System.Text.Json
public class CustomDataAdapter : EventHubDataAdapter
{
    public CustomDataAdapter(Serializer serializer) : base(serializer)
    {
    }

    public override string GetPartitionKey(StreamId streamId)
        => streamId.ToString();

    public override StreamId GetStreamIdentity(EventData queueMessage)
    {
        var guid = Guid.Parse(queueMessage.PartitionKey);
        var ns = (string) queueMessage.Properties["StreamNamespace"];
        return StreamId.Create(ns, guid);
    }

    public override EventData ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        => throw new NotSupportedException("This adapter only supports read");

    protected override IBatchContainer GetBatchContainer(EventHubMessage eventHubMessage)
        => new CustomBatchContainer(eventHubMessage);
}

public class CustomBatchContainer : IBatchContainer
{
    public StreamId StreamId { get; }

    public StreamSequenceToken SequenceToken { get; }

    private readonly byte[] _payload;

    public CustomBatchContainer(EventHubMessage eventHubMessage)
    {
        StreamId = eventHubMessage.StreamId;
        SequenceToken = new EventHubSequenceTokenV2(eventHubMessage.Offset, eventHubMessage.SequenceNumber, 0);
        _payload = eventHubMessage.Payload;
    }

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
    {
        try
        {
            var evt = JsonSerializer.Deserialize<T>(_payload)!;
            return new[] { Tuple.Create(evt, SequenceToken) };
        }
        catch (Exception)
        {
            return new List<Tuple<T, StreamSequenceToken>>();
        }
    }

    public bool ImportRequestContext() => false;
}
