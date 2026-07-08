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
    [Fact]
    public void CollectionsSatisfyMembershipAndRoutingInvariants()
    {
        Gen.Select(Gen.Int[1, 64], Gen.Int[0, 127], Gen.Int[0, 1], Gen.Int[0, 144], static (count, localSeed, localMode, fanoutSeed) =>
        {
            return new ValidSnapshotTestCase(
                count,
                localSeed,
                LocalIsMember: localMode == 0,
                RawFanout: fanoutSeed - 16);
        }).Sample(testCase =>
        {
            var members = CreateSilos(testCase.Count).ToImmutableArray();
            var local = testCase.LocalIsMember
                ? members[testCase.LocalSeed % members.Length]
                : CreateSilo(20000 + testCase.LocalSeed);
            var snapshot = CreateSnapshot(local, members, testCase.RawFanout);

            Assert.Equal(members, snapshot.Members);
            AssertNoDuplicates(nameof(snapshot.Members), snapshot.Members);

            foreach (var member in members)
            {
                Assert.True(snapshot.ContainsMember(member));
            }

            var outsideMember = CreateSilo(25000 + testCase.LocalSeed);
            Assert.False(snapshot.ContainsMember(outsideMember));

            var originatorTargets = snapshot.GetOriginatorTreeTargets();
            var forwardingTargets = snapshot.GetForwardingTreeTargets();

            AssertNoDuplicates(nameof(originatorTargets), originatorTargets);
            AssertNoDuplicates(nameof(forwardingTargets), forwardingTargets);
            AssertSubset(nameof(originatorTargets), originatorTargets, members);
            AssertSubset(nameof(forwardingTargets), forwardingTargets, members);
            Assert.DoesNotContain(local, originatorTargets);
            Assert.DoesNotContain(local, forwardingTargets);
            Assert.Equal(originatorTargets, snapshot.GetOriginatorTreeTargets());
            Assert.Equal(forwardingTargets, snapshot.GetForwardingTreeTargets());

            if (!members.Contains(local))
            {
                Assert.Empty(originatorTargets);
                Assert.Empty(forwardingTargets);
                return;
            }

            var effectiveFanout = GetEffectiveFanout(members.Length, testCase.RawFanout);
            var maxTargets = Math.Max(0, members.Length - 1);
            Assert.True(
                originatorTargets.Length <= Math.Min(effectiveFanout * 2, maxTargets),
                "Originator target count exceeded the expected bound.");
            Assert.True(
                forwardingTargets.Length <= Math.Min(effectiveFanout, maxTargets),
                "Forwarding target count exceeded the expected bound.");
        });
    }

    [Fact]
    public void AntiEntropyPeerSelectionSatisfiesMembershipInvariants()
    {
        Gen.Select(Gen.Int[1, 64], Gen.Int[0, 127], Gen.Int[0, 1], Gen.Int[0, 64], static (count, localSeed, localMode, requestedCount) =>
        {
            return new AntiEntropyTestCase(
                count,
                localSeed,
                LocalIsMember: localMode == 0,
                RequestedCount: requestedCount);
        }).Sample(testCase =>
        {
            var members = CreateSilos(testCase.Count).ToImmutableArray();
            var local = testCase.LocalIsMember
                ? members[testCase.LocalSeed % members.Length]
                : CreateSilo(20000 + testCase.LocalSeed);
            var snapshot = CreateSnapshot(local, members, fanout: 4);

            var selectedPeers = snapshot.SelectAntiEntropyPeers(testCase.RequestedCount);

            AssertNoDuplicates("Anti-entropy peers", selectedPeers);
            AssertSubset("Anti-entropy peers", selectedPeers, members);
            Assert.DoesNotContain(local, selectedPeers);

            var expectedCount = members.Contains(local)
                ? Math.Min(testCase.RequestedCount, Math.Max(0, members.Length - 1))
                : 0;
            Assert.Equal(expectedCount, selectedPeers.Length);
        });
    }

    [Fact]
    public void ConstructorRejectsDuplicateMembers()
    {
        Gen.Select(Gen.Int[1, 64], Gen.Int[0, 63], static (count, duplicateSeed) => (Count: count, DuplicateSeed: duplicateSeed))
            .Sample(testCase =>
            {
                var members = CreateSilos(testCase.Count);
                var duplicate = members[testCase.DuplicateSeed % members.Length];
                var invalidMembers = members.Append(duplicate).ToImmutableArray();

                var exception = Assert.Throws<ArgumentException>(() => CreateSnapshot(
                    members[0],
                    invalidMembers,
                    fanout: 4));
                Assert.Equal("members", exception.ParamName);
            });
    }

    private static DisseminationMembershipSnapshot CreateSnapshot(
        SiloAddress localSilo,
        ImmutableArray<SiloAddress> members,
        int fanout) => new(
            new MembershipVersion(1),
            localSilo,
            members,
            CreateOverlayOptions(fanout));

    private static DisseminationOverlayOptions CreateOverlayOptions(int fanout) => new()
    {
        FanOutFactor = _ => fanout,
    };

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
            Assert.True(expected.Contains(value), $"{name} contains {value} outside the expected membership set.");
        }
    }

    private static SiloAddress CreateSilo(int port) => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), port);

    private static SiloAddress[] CreateSilos(int count) =>
        Enumerable.Range(11111, count).Select(CreateSilo).OrderBy(static silo => silo).ToArray();

    private readonly record struct ValidSnapshotTestCase(
        int Count,
        int LocalSeed,
        bool LocalIsMember,
        int RawFanout);

    private readonly record struct AntiEntropyTestCase(
        int Count,
        int LocalSeed,
        bool LocalIsMember,
        int RequestedCount);
}
