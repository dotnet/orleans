using System;
using System.Collections.Generic;
using System.Threading;
using Orleans.Runtime;
using Orleans.Streams;
using OrleansAWSUtils.Streams;
using SQSMessage = Amazon.SQS.Model.Message;

namespace Orleans.Streaming.SQS.Streams;

/// <summary>
/// Default SQS Stream data adapter.  Users may subclass to override event data to stream mapping.
/// </summary>

public class SQSDataAdapter : ISQSDataAdapter
{
    private readonly Serialization.Serializer serializer;

    public SQSDataAdapter(Serialization.Serializer serializer)
    {
        this.serializer = serializer;
    }

    /// <summary>
    /// Convert an SQS Message to a batch container
    /// </summary>
    /// <param name="sqsMessage"></param>
    /// <returns></returns>
    public virtual IBatchContainer GetBatchContainer(SQSMessage sqsMessage, ref long sequenceNumber)
    {
        return SQSBatchContainer.FromSQSMessage(
            serializer.GetSerializer<SQSBatchContainer>(),
            sqsMessage,
            Interlocked.Increment(ref sequenceNumber));
    }

    public virtual SQSMessage ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        if (token != null) throw new ArgumentException("SQS streams currently does not support non-null StreamSequenceToken.", nameof(token));
        return SQSBatchContainer.ToSQSMessage(
            serializer.GetSerializer<SQSBatchContainer>(),
            streamId,
            events,
            requestContext);
    }
}
