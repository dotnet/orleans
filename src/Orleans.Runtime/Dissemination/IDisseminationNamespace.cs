using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationNamespace
{
    DisseminationNamespace Name { get; }

    DisseminationNamespaceOptions Options { get; }

    IEnumerable<DigestEntry> Digests { get; }

    long GetVersion(DisseminationKey key);

    bool TryCreateRepairValue(
        DisseminationKey key,
        long peerVersion,
        out DisseminationValue value);

    ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken);
}
