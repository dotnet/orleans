using System;

namespace Orleans.Streams;

internal interface IQueueCacheCursorBatchDelivery
{
    IDisposable ProtectDeliveryBatch();

    void RecordDeliveryFailure(IBatchContainer batch);
}
