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

    int CompareVersion(DisseminationDigest left, DisseminationDigest right);

    bool IsObsolete(DisseminationDigest digest);

    ValueTask<DisseminationValue?> GetValue(
        DisseminationDigest digest,
        DisseminationDigest? peerDigest,
        CancellationToken cancellationToken);

    ValueTask<DisseminationApplyResult> ApplyValue(DisseminationValue value, CancellationToken cancellationToken);

    ValueTask OnFallbackRequired(SiloAddress? peer, DisseminationDigest digest, CancellationToken cancellationToken);
}

internal enum DisseminationMembershipScope
{
    ActiveMembers,
    AllMembers,
}
