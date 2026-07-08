using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Dissemination;

internal readonly record struct DisseminationTopicValue(
    DisseminationTopicDigest Digest,
    ReadOnlyMemory<byte> Payload);

internal interface IDisseminationService
{
    ValueTask<bool> Publish(
        IDisseminationTopic topic,
        DisseminationTopicValue value,
        CancellationToken cancellationToken);
}
