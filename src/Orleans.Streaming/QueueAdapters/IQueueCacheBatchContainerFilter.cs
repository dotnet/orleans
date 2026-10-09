namespace Orleans.Streams;

internal interface IQueueCacheBatchContainerFilter
{
    IBatchContainer FilterFrom(StreamSequenceToken inclusiveStartToken);

    IBatchContainer? FilterAfter(StreamSequenceToken exclusiveStartToken);
}
