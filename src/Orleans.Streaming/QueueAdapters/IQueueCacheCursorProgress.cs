namespace Orleans.Streams;

internal interface IQueueCacheCursorProgress
{
    StreamSequenceToken? SafeSequenceToken { get; }

    void SetDeliveredThrough(StreamSequenceToken token);

    void RecordDeliverySuccess();
}
