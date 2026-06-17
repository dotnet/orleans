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
    private static readonly DisseminationValueKey MembershipKey = DisseminationValueKey.FromString("cluster");
    private static readonly HashSet<string> SupportedPayloadKinds = new(StringComparer.Ordinal)
    {
        DisseminationTopicNames.MembershipSnapshot,
    };

    public string Name => DisseminationTopicNames.Membership;

    public int ProtocolVersion => 2;

    public DisseminationTopicOptions Options => options.CurrentValue.Dissemination;

    public IReadOnlySet<string> PayloadKinds => SupportedPayloadKinds;

    public bool IsEnabled => Options.Enabled;

    public DisseminationItem CreateItem(SiloAddress origin, MembershipTableSnapshot snapshot)
    {
        var payload = serializer.SerializeToArray(snapshot);
        return new DisseminationItem
        {
            Id = new DisseminationItemId(Name, MembershipKey, snapshot.Version.Value, DisseminationTopicNames.MembershipSnapshot),
            Root = origin,
            ExpiresAt = timeProvider.GetUtcNow() + Options.StaleItemTtl,
            Payload = payload,
        };
    }

    public IReadOnlyList<DisseminationItemId> GetDigests()
    {
        var snapshot = membershipManager.CurrentSnapshot;
        return new[]
        {
            new DisseminationItemId(Name, MembershipKey, snapshot.Version.Value, DisseminationTopicNames.MembershipSnapshot),
        };
    }

    public int CompareVersion(DisseminationItemId left, DisseminationItemId right) => left.Version.CompareTo(right.Version);

    public bool IsObsolete(DisseminationItemId id) =>
        !string.Equals(id.PayloadKind, DisseminationTopicNames.MembershipSnapshot, StringComparison.Ordinal)
        || id.Key != MembershipKey
        || membershipManager.CurrentSnapshot.Version.Value > id.Version;

    public ValueTask<DisseminationItem?> GetItem(DisseminationItemId id, CancellationToken cancellationToken)
    {
        if (!string.Equals(id.PayloadKind, DisseminationTopicNames.MembershipSnapshot, StringComparison.Ordinal)
            || id.Key != MembershipKey)
        {
            return ValueTask.FromResult<DisseminationItem?>(null);
        }

        var snapshot = membershipManager.CurrentSnapshot;
        if (snapshot.Version.Value < id.Version)
        {
            return ValueTask.FromResult<DisseminationItem?>(null);
        }

        return ValueTask.FromResult<DisseminationItem?>(CreateItem(localSiloDetails.SiloAddress, snapshot));
    }

    public async ValueTask<DisseminationApplyResult> ApplyItem(DisseminationItem item, CancellationToken cancellationToken)
    {
        if (!string.Equals(item.Id.PayloadKind, DisseminationTopicNames.MembershipSnapshot, StringComparison.Ordinal)
            || item.Id.Key != MembershipKey)
        {
            return DisseminationApplyResult.Rejected;
        }

        var snapshot = serializer.Deserialize<MembershipTableSnapshot>(item.Payload);
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

    public async ValueTask OnFallbackRequired(SiloAddress peer, DisseminationItemId id, CancellationToken cancellationToken)
    {
        if (Options.FallbackEnabled)
        {
            await membershipManager.Refresh(new MembershipVersion(id.Version), cancellationToken);
        }
    }
}
