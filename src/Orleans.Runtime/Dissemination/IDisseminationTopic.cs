using System;
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

    bool IsEnabled { get; }

    IReadOnlyList<DisseminationTopicDigest> GetDigests();

    int CompareVersion(DisseminationTopicDigest left, DisseminationTopicDigest right);

    bool IsObsolete(DisseminationTopicDigest digest);

    ValueTask<DisseminationTopicValue?> GetValue(
        DisseminationTopicDigest digest,
        DisseminationTopicDigest? peerDigest,
        CancellationToken cancellationToken);

    ValueTask<DisseminationApplyResult> ApplyValue(DisseminationValue value, CancellationToken cancellationToken);

    ValueTask OnFallbackRequired(SiloAddress? peer, DisseminationTopicDigest digest, CancellationToken cancellationToken);
}

internal enum DisseminationMembershipScope
{
    ActiveMembers,
    AllMembers,
}
