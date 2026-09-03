using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="SQSDataAdapter"/> class.
    /// </summary>
    /// <param name="serializer">The serializer used to serialize and deserialize stream batches.</param>
    public SQSDataAdapter(Serialization.Serializer serializer)
    {
        this.serializer = serializer;
    }

    /// <summary>
    /// Converts an SQS message to an Orleans stream batch.
    /// </summary>
    /// <param name="sqsMessage">The SQS message to convert.</param>
    /// <param name="sequenceId">The locally assigned sequence number for the stream batch.</param>
    /// <returns>The stream batch represented by <paramref name="sqsMessage"/>.</returns>
    public virtual IBatchContainer FromQueueMessage(SQSMessage sqsMessage, long sequenceId)
    {
        return SQSBatchContainer.FromSQSMessage(
            serializer.GetSerializer<SQSBatchContainer>(),
            sqsMessage,
            sequenceId);
    }

    /// <summary>
    /// Converts a batch of Orleans stream events to an SQS message.
    /// </summary>
    /// <typeparam name="T">The type of the stream events.</typeparam>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="events">The stream events to include in the message.</param>
    /// <param name="token">The stream sequence token, which must be <see langword="null"/>.</param>
    /// <param name="requestContext">The request context to include in the message.</param>
    /// <returns>An SQS message containing the serialized stream batch.</returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is not <see langword="null"/>.</exception>
    public virtual SQSMessage ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token, Dictionary<string, object>? requestContext)
    {
        if (token != null) throw new ArgumentException("SQS streams currently does not support non-null StreamSequenceToken.", nameof(token));
        return SQSBatchContainer.ToSQSMessage(
            serializer.GetSerializer<SQSBatchContainer>(),
            streamId,
            events,
            requestContext);
    }
}
