using Orleans.Streams;
using SQSMessage = Amazon.SQS.Model.Message;

namespace Orleans.Streaming.SQS.Streams;

/// <summary>
/// Converts Orleans stream batches to and from Amazon SQS messages.
/// </summary>
public interface ISQSDataAdapter : IQueueDataAdapter<SQSMessage, IBatchContainer>;
