using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationTopic
{
    string Name { get; }

    int ProtocolVersion { get; }

    DisseminationMembershipScope MembershipScope { get; }

    DisseminationTopicOptions Options { get; }

    IReadOnlySet<string> PayloadKinds { get; }

    bool IsEnabled { get; }

    IReadOnlyList<DisseminationDigest> GetDigests();

    int CompareVersion(DisseminationDigest left, DisseminationDigest right);

    bool IsObsolete(DisseminationDigest digest);

    ValueTask<DisseminationValue?> GetValue(DisseminationDigest digest, CancellationToken cancellationToken);

    ValueTask<DisseminationApplyResult> ApplyValue(DisseminationValue value, CancellationToken cancellationToken);

    ValueTask OnFallbackRequired(SiloAddress peer, DisseminationDigest digest, CancellationToken cancellationToken);
}

internal enum DisseminationMembershipScope
{
    ActiveMembers,
    AllMembers,
}
