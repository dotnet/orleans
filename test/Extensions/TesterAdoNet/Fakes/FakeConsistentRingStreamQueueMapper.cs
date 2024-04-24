using Orleans.Runtime;
using Orleans.Streams;

namespace Tester.AdoNet.Fakes;

internal class FakeConsistentRingStreamQueueMapper(
    Func<IEnumerable<QueueId>> getAllQueues = null,
    Func<StreamId, QueueId> getQueueForStream = null,
    Func<IRingRange, IEnumerable<QueueId>> getQueuesForRange = null) : IConsistentRingStreamQueueMapper
{
    private readonly QueueId _defaultQueueId = QueueId.GetQueueId("QueueName", 1, 1);

    public IEnumerable<QueueId> GetAllQueues() => getAllQueues is not null ? getAllQueues() : [_defaultQueueId];

    public QueueId GetQueueForStream(StreamId streamId) => getQueueForStream is not null ? getQueueForStream(streamId) : _defaultQueueId;

    public IEnumerable<QueueId> GetQueuesForRange(IRingRange range) => getQueuesForRange is not null ? getQueuesForRange(range) : [_defaultQueueId];
}