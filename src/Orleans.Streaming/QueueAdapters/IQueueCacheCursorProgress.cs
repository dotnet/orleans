namespace Orleans.Streams;

internal interface IQueueCacheCursorProgress
{
    StreamSequenceToken? SafeSequenceToken { get; }

    void AdvancePast(StreamSequenceToken token);

    void RecordDeliverySuccess();
}
