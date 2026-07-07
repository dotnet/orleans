using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationService
{
    ValueTask<bool> Publish(
        string topicName,
        DisseminationValue value,
        IReadOnlyCollection<SiloAddress>? targetPeers,
        CancellationToken cancellationToken);
}
