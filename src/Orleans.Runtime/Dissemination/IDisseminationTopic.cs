using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationTopic
{
    string Name { get; }

    int ProtocolVersion { get; }

    DisseminationTopicOptions Options { get; }

    IReadOnlySet<string> PayloadKinds { get; }

    bool IsEnabled { get; }

    IReadOnlyList<DisseminationItemId> GetDigests();

    int CompareVersion(DisseminationItemId left, DisseminationItemId right);

    bool IsObsolete(DisseminationItemId id);

    ValueTask<DisseminationItem?> GetItem(DisseminationItemId id, CancellationToken cancellationToken);

    ValueTask<DisseminationApplyResult> ApplyItem(DisseminationItem item, CancellationToken cancellationToken);

    ValueTask OnFallbackRequired(SiloAddress peer, DisseminationItemId id, CancellationToken cancellationToken);
}
