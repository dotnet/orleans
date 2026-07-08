using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationNamespace
{
    string Name { get; }

    DisseminationGroup Group { get; }

    DisseminationNamespaceOptions Options { get; }

    IReadOnlyDictionary<string, long> GetDigest();

    long GetVersion(string key);

    bool TryCreateRepairValue(
        string key,
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
