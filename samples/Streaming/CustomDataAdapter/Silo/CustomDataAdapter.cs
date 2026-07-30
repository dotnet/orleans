using System.Text.Json;
using Azure.Messaging.EventHubs;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.EventHubs;
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

[GenerateSerializer, Immutable]
public sealed class CustomBatchContainer : IBatchContainer
{
    [Id(0)]
    private readonly EventHubMessage _eventHubMessage;

    [Id(1)]
    public StreamSequenceToken SequenceToken { get; }

    public StreamId StreamId => _eventHubMessage.StreamId;

    public CustomBatchContainer(EventHubMessage eventHubMessage)
    {
        _eventHubMessage = eventHubMessage;
        SequenceToken = new EventHubSequenceTokenV2(_eventHubMessage.Offset, _eventHubMessage.SequenceNumber, 0);
    }

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
    {
        try
        {
            var evt = JsonSerializer.Deserialize<T>(_eventHubMessage.Payload)!;
            return new[] { Tuple.Create(evt, SequenceToken) };
        }
        catch (Exception)
        {
            return Array.Empty<Tuple<T, StreamSequenceToken>>();
        }
    }

    public bool ImportRequestContext() => false;
}
