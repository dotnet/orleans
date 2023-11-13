using Orleans.Streams;
using SQSMessage = Amazon.SQS.Model.Message;

namespace Orleans.Streaming.SQS.Streams;
public interface ISQSDataAdapter : IQueueDataAdapter<SQSMessage>
{
    IBatchContainer GetBatchContainer(SQSMessage sqsMessage, ref long sequenceNumber);
}
