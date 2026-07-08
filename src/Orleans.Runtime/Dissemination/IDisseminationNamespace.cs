using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationNamespace
{
    DisseminationNamespace Name { get; }

    DisseminationGroup Group { get; }

    DisseminationNamespaceOptions Options { get; }

    IReadOnlyDictionary<DisseminationKey, long> GetDigest();

    long GetVersion(DisseminationKey key);

    bool TryCreateRepairValue(
        DisseminationKey key,
        long peerVersion,
        out DisseminationValue value);

    ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken);
}

internal enum DisseminationGroup
{
    ActiveMembers,
    AllMembers,
}
