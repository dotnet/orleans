using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationTopic
{
    string Name { get; }

    DisseminationMembershipScope MembershipScope { get; }

    DisseminationTopicOptions Options { get; }

    IReadOnlyList<DisseminationTopicDigest> GetDigests();

    bool IsObsolete(DisseminationTopicDigest digest);

    bool TryCreateRepairValue(
        DisseminationTopicDigest localDigest,
        DisseminationTopicDigest peerDigest,
        out DisseminationTopicValue value);

    ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationTopicValue value,
        CancellationToken cancellationToken);
}

internal enum DisseminationMembershipScope
{
    ActiveMembers,
    AllMembers,
}
