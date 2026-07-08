using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationService
{
    ValueTask<bool> Publish(
        IDisseminationTopic topic,
        DisseminationValue value,
        CancellationToken cancellationToken);
}
