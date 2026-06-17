using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal sealed class MembershipDisseminationTopic(
    IMembershipManager membershipManager,
    IOptionsMonitor<ClusterMembershipOptions> options,
    Serializer serializer,
    TimeProvider timeProvider,
    ILocalSiloDetails localSiloDetails) : IDisseminationTopic
{
    private const string MembershipKey = "cluster";
    private static readonly HashSet<string> SupportedPayloadKinds = new(StringComparer.Ordinal)
    {
        DisseminationTopicNames.MembershipSnapshot,
    };

    public string Name => DisseminationTopicNames.Membership;

    public int ProtocolVersion => 2;

    public DisseminationMembershipScope MembershipScope => DisseminationMembershipScope.AllMembers;

    public DisseminationTopicOptions Options => options.CurrentValue.Dissemination;

    public IReadOnlySet<string> PayloadKinds => SupportedPayloadKinds;

    public bool IsEnabled => Options.Enabled;

    public DisseminationValue CreateItem(SiloAddress origin, MembershipTableSnapshot snapshot)
    {
        var payload = serializer.SerializeToArray(snapshot);
        return new DisseminationValue
        {
            Digest = new DisseminationDigest(Name, MembershipKey, snapshot.Version.Value, DisseminationTopicNames.MembershipSnapshot),
            Root = origin,
            ExpiresAt = timeProvider.GetUtcNow() + Options.StaleItemTtl,
            Payload = payload,
        };
    }

    public IReadOnlyList<DisseminationDigest> GetDigests()
    {
        var snapshot = membershipManager.CurrentSnapshot;
        return new[]
        {
            new DisseminationDigest(Name, MembershipKey, snapshot.Version.Value, DisseminationTopicNames.MembershipSnapshot),
        };
    }

    public int CompareVersion(DisseminationDigest left, DisseminationDigest right) => left.Version.CompareTo(right.Version);

    public bool IsObsolete(DisseminationDigest digest) =>
        !string.Equals(digest.PayloadKind, DisseminationTopicNames.MembershipSnapshot, StringComparison.Ordinal)
        || digest.Key != MembershipKey
        || membershipManager.CurrentSnapshot.Version.Value > digest.Version;

    public ValueTask<DisseminationValue?> GetValue(DisseminationDigest digest, CancellationToken cancellationToken)
    {
        if (!string.Equals(digest.PayloadKind, DisseminationTopicNames.MembershipSnapshot, StringComparison.Ordinal)
            || digest.Key != MembershipKey)
        {
            return ValueTask.FromResult<DisseminationValue?>(null);
        }

        var snapshot = membershipManager.CurrentSnapshot;
        if (snapshot.Version.Value < digest.Version)
        {
            return ValueTask.FromResult<DisseminationValue?>(null);
        }

        return ValueTask.FromResult<DisseminationValue?>(CreateItem(localSiloDetails.SiloAddress, snapshot));
    }

    public async ValueTask<DisseminationApplyResult> ApplyValue(DisseminationValue value, CancellationToken cancellationToken)
    {
        if (!string.Equals(value.Digest.PayloadKind, DisseminationTopicNames.MembershipSnapshot, StringComparison.Ordinal)
            || value.Digest.Key != MembershipKey)
        {
            return DisseminationApplyResult.Rejected;
        }

        var snapshot = serializer.Deserialize<MembershipTableSnapshot>(value.Payload);
        var currentVersion = membershipManager.CurrentSnapshot.Version;
        if (snapshot.Version < currentVersion)
        {
            return DisseminationApplyResult.Obsolete;
        }

        if (snapshot.Version == currentVersion)
        {
            return DisseminationApplyResult.Duplicate;
        }

        await membershipManager.ProcessGossipSnapshot(snapshot, cancellationToken);
        return DisseminationApplyResult.Applied;
    }

    public async ValueTask OnFallbackRequired(SiloAddress peer, DisseminationDigest digest, CancellationToken cancellationToken)
    {
        if (Options.FallbackEnabled)
        {
            await membershipManager.Refresh(new MembershipVersion(digest.Version), cancellationToken);
        }
    }
}
