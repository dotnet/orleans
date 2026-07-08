#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using CsCheck;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
public class DisseminationMembershipSnapshotTests
{
    private static readonly DisseminationGroup[] Groups = [DisseminationGroup.AllMembers, DisseminationGroup.ActiveMembers];

    [Fact]
    public void CollectionsSatisfyMembershipAndRoutingInvariants()
    {
        Gen.Select(Gen.Int[1, 64], Gen.ULong, Gen.Int[0, 127], Gen.Int[0, 1], Gen.Int[0, 144], static (count, activeMask, localSeed, localMode, fanoutSeed) =>
        {
            return new ValidSnapshotTestCase(
                count,
                activeMask,
                localSeed,
                LocalIsMember: localMode == 0,
                RawFanout: fanoutSeed - 16);
        }).Sample(testCase =>
        {
            var allMembers = CreateSilos(testCase.Count).ToImmutableArray();
            var activeMembers = CreateActiveMembers(allMembers, testCase.ActiveMask);
            var local = testCase.LocalIsMember
                ? allMembers[testCase.LocalSeed % allMembers.Length]
                : CreateSilo(20000 + testCase.LocalSeed);
            var snapshot = CreateSnapshot(local, allMembers, activeMembers, testCase.RawFanout);

            Assert.Equal(allMembers, snapshot.AllMembers);
            Assert.Equal(activeMembers, snapshot.ActiveMembers);
            AssertNoDuplicates(nameof(snapshot.AllMembers), snapshot.AllMembers);
            AssertNoDuplicates(nameof(snapshot.ActiveMembers), snapshot.ActiveMembers);
            AssertSubset(nameof(snapshot.ActiveMembers), snapshot.ActiveMembers, snapshot.AllMembers);

            foreach (var member in allMembers)
            {
                Assert.True(snapshot.ContainsMember(member, DisseminationGroup.AllMembers));
            }

            foreach (var member in activeMembers)
            {
                Assert.True(snapshot.ContainsMember(member, DisseminationGroup.ActiveMembers));
            }

            var outsideMember = CreateSilo(25000 + testCase.LocalSeed);
            Assert.False(snapshot.ContainsMember(outsideMember, DisseminationGroup.AllMembers));
            Assert.False(snapshot.ContainsMember(outsideMember, DisseminationGroup.ActiveMembers));

            foreach (var group in Groups)
            {
                var groupMembers = GetMembers(snapshot, group);
                var originatorTargets = snapshot.GetOriginatorTreeTargets(group);
                var forwardingTargets = snapshot.GetForwardingTreeTargets(group);

                AssertNoDuplicates($"{group} originator targets", originatorTargets);
                AssertNoDuplicates($"{group} forwarding targets", forwardingTargets);
                AssertSubset($"{group} originator targets", originatorTargets, groupMembers);
                AssertSubset($"{group} forwarding targets", forwardingTargets, groupMembers);
                Assert.DoesNotContain(local, originatorTargets);
                Assert.DoesNotContain(local, forwardingTargets);
                Assert.Equal(originatorTargets, snapshot.GetOriginatorTreeTargets(group));
                Assert.Equal(forwardingTargets, snapshot.GetForwardingTreeTargets(group));

                if (!groupMembers.Contains(local))
                {
                    Assert.Empty(originatorTargets);
                    Assert.Empty(forwardingTargets);
                    continue;
                }

                var effectiveFanout = GetEffectiveFanout(groupMembers.Length, testCase.RawFanout);
                var maxTargets = Math.Max(0, groupMembers.Length - 1);
                Assert.True(
                    originatorTargets.Count <= Math.Min(effectiveFanout * 2, maxTargets),
                    $"{group} originator target count exceeded the expected bound.");
                Assert.True(
                    forwardingTargets.Count <= Math.Min(effectiveFanout, maxTargets),
                    $"{group} forwarding target count exceeded the expected bound.");
            }
        });
    }

    [Fact]
    public void AntiEntropyPeerSelectionSatisfiesMembershipInvariants()
    {
        Gen.Select(Gen.Int[1, 64], Gen.ULong, Gen.Int[0, 127], Gen.Int[0, 1], Gen.Int[0, 64], static (count, activeMask, localSeed, localMode, requestedCount) =>
        {
            return new AntiEntropyTestCase(
                count,
                activeMask,
                localSeed,
                LocalIsMember: localMode == 0,
                RequestedCount: requestedCount);
        }).Sample(testCase =>
        {
            var allMembers = CreateSilos(testCase.Count).ToImmutableArray();
            var activeMembers = CreateActiveMembers(allMembers, testCase.ActiveMask);
            var local = testCase.LocalIsMember
                ? allMembers[testCase.LocalSeed % allMembers.Length]
                : CreateSilo(20000 + testCase.LocalSeed);
            var snapshot = CreateSnapshot(local, allMembers, activeMembers, fanout: 4);

            foreach (var group in Groups)
            {
                var groupMembers = GetMembers(snapshot, group);
                Span<SiloAddress> peers = new SiloAddress[testCase.RequestedCount];
                snapshot.SelectAntiEntropyPeers(group, ref peers);
                var selectedPeers = peers.ToArray();

                AssertNoDuplicates($"{group} anti-entropy peers", selectedPeers);
                AssertSubset($"{group} anti-entropy peers", selectedPeers, groupMembers);
                Assert.DoesNotContain(local, selectedPeers);

                var expectedCount = groupMembers.Contains(local)
                    ? Math.Min(testCase.RequestedCount, Math.Max(0, groupMembers.Length - 1))
                    : 0;
                Assert.Equal(expectedCount, selectedPeers.Length);
            }
        });
    }

    [Fact]
    public void ConstructorRejectsDuplicateAllMembers()
    {
        Gen.Select(Gen.Int[1, 64], Gen.Int[0, 63], static (count, duplicateSeed) => (Count: count, DuplicateSeed: duplicateSeed))
            .Sample(testCase =>
            {
                var allMembers = CreateSilos(testCase.Count);
                var duplicate = allMembers[testCase.DuplicateSeed % allMembers.Length];
                var invalidAllMembers = allMembers.Append(duplicate).ToImmutableArray();

                var exception = Assert.Throws<ArgumentException>(() => CreateSnapshot(
                    allMembers[0],
                    invalidAllMembers,
                    [allMembers[0]],
                    fanout: 4));
                Assert.Equal("allMembers", exception.ParamName);
            });
    }

    [Fact]
    public void ConstructorRejectsActiveMembersOutsideAllMembers()
    {
        Gen.Select(Gen.Int[1, 64], Gen.ULong, Gen.Int[0, 63], Gen.Int[0, 127], static (count, activeMask, localSeed, outsideSeed) =>
        {
            return new InvalidActiveMembersTestCase(count, activeMask, localSeed, outsideSeed);
        }).Sample(testCase =>
        {
            var allMembers = CreateSilos(testCase.Count).ToImmutableArray();
            var activeMembers = CreateActiveMembers(allMembers, testCase.ActiveMask)
                .Add(CreateSilo(20000 + testCase.OutsideSeed));
            var local = allMembers[testCase.LocalSeed % allMembers.Length];

            var exception = Assert.Throws<ArgumentException>(() => CreateSnapshot(local, allMembers, activeMembers, fanout: 4));
            Assert.Equal("activeMembers", exception.ParamName);
        });
    }

    private static DisseminationMembershipSnapshot CreateSnapshot(
        SiloAddress localSilo,
        ImmutableArray<SiloAddress> allMembers,
        ImmutableArray<SiloAddress> activeMembers,
        int fanout) => new(
            new MembershipVersion(1),
            localSilo,
            allMembers,
            activeMembers,
            CreateOverlayOptions(fanout));

    private static DisseminationOverlayOptions CreateOverlayOptions(int fanout) => new()
    {
        FanOutFactor = _ => fanout,
    };

    private static ImmutableArray<SiloAddress> CreateActiveMembers(ImmutableArray<SiloAddress> allMembers, ulong activeMask)
    {
        var result = ImmutableArray.CreateBuilder<SiloAddress>();
        for (var i = 0; i < allMembers.Length; i++)
        {
            if ((activeMask & (1UL << i)) != 0)
            {
                result.Add(allMembers[i]);
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<SiloAddress> GetMembers(DisseminationMembershipSnapshot snapshot, DisseminationGroup group) =>
        group == DisseminationGroup.AllMembers ? snapshot.AllMembers : snapshot.ActiveMembers;

    private static int GetEffectiveFanout(int memberCount, int rawFanout) =>
        memberCount <= 1 ? 1 : Math.Clamp(rawFanout, 1, memberCount);

    private static void AssertNoDuplicates(string name, IEnumerable<SiloAddress> values)
    {
        var array = values.ToArray();
        Assert.True(array.Length == array.Distinct().Count(), $"{name} contains duplicates.");
    }

    private static void AssertSubset(string name, IEnumerable<SiloAddress> values, IEnumerable<SiloAddress> expectedValues)
    {
        var expected = expectedValues.ToHashSet();
        foreach (var value in values)
        {
            Assert.True(expected.Contains(value), $"{name} contains {value} outside the expected membership group.");
        }
    }

    private static SiloAddress CreateSilo(int port) => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), port);

    private static SiloAddress[] CreateSilos(int count) =>
        Enumerable.Range(11111, count).Select(CreateSilo).OrderBy(static silo => silo).ToArray();

    private readonly record struct ValidSnapshotTestCase(
        int Count,
        ulong ActiveMask,
        int LocalSeed,
        bool LocalIsMember,
        int RawFanout);

    private readonly record struct AntiEntropyTestCase(
        int Count,
        ulong ActiveMask,
        int LocalSeed,
        bool LocalIsMember,
        int RequestedCount);

    private readonly record struct InvalidActiveMembersTestCase(
        int Count,
        ulong ActiveMask,
        int LocalSeed,
        int OutsideSeed);
}
