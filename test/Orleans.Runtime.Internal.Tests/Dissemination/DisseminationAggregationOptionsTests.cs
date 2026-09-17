#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dissemination")]
public class DisseminationAggregationOptionsTests
{
    [Fact]
    public void DefaultsAreBoundedAndOptIn()
    {
        var options = new DisseminationOptions();
        var load = new DeploymentLoadPublisherOptions();
        var membership = new ClusterMembershipOptions();

        Assert.Equal(8, options.Overlay.AggregationFanOutFactor);
        Assert.Equal(8192, options.Overlay.MaxAntiEntropyBatchItems);
        Assert.Equal(1048576, options.Overlay.MaxAntiEntropyBatchBytes);
        Assert.Equal(options.MaxBatchItems, options.Overlay.MaxAntiEntropyBatchItems);
        Assert.Equal(options.MaxBatchBytes, options.Overlay.MaxAntiEntropyBatchBytes);
        Assert.Equal(new DisseminationNamespaceOptions().MaxCoalescingDelay, load.Dissemination.MaxCoalescingDelay);
        Assert.Equal(8192, load.Dissemination.MaxPendingItemCount);
        Assert.Equal(TimeSpan.FromSeconds(5), load.Dissemination.ExpectedUpdateCadence);
        Assert.Equal(TimeSpan.FromSeconds(1), load.DeploymentLoadPublisherRefreshTime);
        Assert.False(options.Enabled);
        Assert.False(new DisseminationNamespaceOptions().Enabled);
        Assert.False(load.Dissemination.Enabled);
        Assert.False(membership.Dissemination.Enabled);
        Assert.Equal(DisseminationPriority.High, membership.Dissemination.Priority);
        Assert.Equal(ValidateOptionsResult.Success, Validate(options));
        Assert.Equal(ValidateOptionsResult.Success, new DeploymentLoadPublisherOptionsValidator().Validate(Options.DefaultName, load));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void ValidatorAcceptsPositiveBounds(int fanout, int items, int bytes)
    {
        var options = new DisseminationOptions();
        options.Overlay.AggregationFanOutFactor = fanout;
        options.Overlay.MaxAntiEntropyBatchItems = items;
        options.Overlay.MaxAntiEntropyBatchBytes = bytes;

        Assert.Equal(ValidateOptionsResult.Success, Validate(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ValidatorRejectsNonPositiveAggregationFanOut(int value)
    {
        var options = new DisseminationOptions();
        options.Overlay.AggregationFanOutFactor = value;

        AssertValidationFailure(options, "AggregationFanOutFactor must be greater than 0.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ValidatorRejectsNonPositiveAntiEntropyItemLimit(int value)
    {
        var options = new DisseminationOptions();
        options.Overlay.MaxAntiEntropyBatchItems = value;

        AssertValidationFailure(options, "MaxAntiEntropyBatchItems must be greater than 0.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ValidatorRejectsNonPositiveAntiEntropyByteLimit(int value)
    {
        var options = new DisseminationOptions();
        options.Overlay.MaxAntiEntropyBatchBytes = value;

        AssertValidationFailure(options, "MaxAntiEntropyBatchBytes must be greater than 0.");
    }

    [Fact]
    public void AntiEntropyLimitsAreIndependentOfBroadcastLimits()
    {
        var options = new DisseminationOptions { MaxBatchItems = 1, MaxBatchBytes = 1 };

        Assert.Equal(8192, options.Overlay.MaxAntiEntropyBatchItems);
        Assert.Equal(1048576, options.Overlay.MaxAntiEntropyBatchBytes);
        Assert.Equal(ValidateOptionsResult.Success, Validate(options));

        options.MaxBatchItems = 16384;
        options.MaxBatchBytes = 2 * 1024 * 1024;
        options.Overlay.MaxAntiEntropyBatchItems = 2;
        options.Overlay.MaxAntiEntropyBatchBytes = 3;

        Assert.Equal(16384, options.MaxBatchItems);
        Assert.Equal(2 * 1024 * 1024, options.MaxBatchBytes);
        Assert.Equal(2, options.Overlay.MaxAntiEntropyBatchItems);
        Assert.Equal(3, options.Overlay.MaxAntiEntropyBatchBytes);
        Assert.Equal(ValidateOptionsResult.Success, Validate(options));
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(2048)]
    public void AggregationTopologyHasEightWayBranchesAndDepthFourAtScale(int memberCount)
    {
        var members = CreateSilos(memberCount);
        var overlay = new DisseminationOverlayOptions();
        var children = new Dictionary<SiloAddress, ImmutableArray<SiloAddress>>(memberCount);
        var roots = new List<SiloAddress>();

        Assert.Equal(32, overlay.GetFanOutFactor(memberCount));
        for (var index = 0; index < members.Length; index++)
        {
            var silo = members[index];
            var snapshot = new DisseminationMembershipSnapshot(new MembershipVersion(1), silo, members, overlay);
            children.Add(silo, snapshot.AggregationChildren);

            Assert.InRange(snapshot.AggregationChildren.Length, 0, 8);
            Assert.DoesNotContain(silo, snapshot.AggregationChildren);
            Assert.Equal(snapshot.AggregationChildren.Length, snapshot.AggregationChildren.Distinct().Count());
            Assert.Equal(members.Skip(32 * (index + 1)).Take(32), snapshot.ForwardingTreeTargets);
            Assert.Equal(snapshot.AggregationChildren, snapshot.GetForwardingTargets(DisseminationRoutingMode.AggregationTree));
            if (snapshot.IsAggregationRoot)
            {
                roots.Add(silo);
                Assert.Equal(8, snapshot.AggregationChildren.Length);
                Assert.Equal(members.Skip(1).Take(8), snapshot.AggregationChildren);
                Assert.Equal(snapshot.AggregationChildren, snapshot.GetOriginatorTargets(DisseminationRoutingMode.AggregationTree));
            }
            else
            {
                Assert.Equal(members[0], Assert.Single(snapshot.GetOriginatorTargets(DisseminationRoutingMode.AggregationTree)));
            }
        }

        Assert.Equal(members[0], Assert.Single(roots));
        Assert.Equal(memberCount - 1, children.Values.Sum(targets => targets.Length));
        var reached = new HashSet<SiloAddress> { members[0] };
        var pending = new Queue<(SiloAddress Silo, int Depth)>();
        pending.Enqueue((members[0], 0));
        var maxDepth = 0;
        while (pending.TryDequeue(out var current))
        {
            maxDepth = Math.Max(maxDepth, current.Depth);
            foreach (var child in children[current.Silo])
            {
                Assert.True(reached.Add(child), $"Member {child} was reached more than once.");
                pending.Enqueue((child, current.Depth + 1));
            }
        }

        Assert.Equal(members, reached.Order());
        Assert.Equal(4, maxDepth);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(32)]
    [InlineData(int.MaxValue)]
    public void MembershipFanOutSelectorDoesNotChangeAggregationTopology(int selectedFanout)
    {
        var members = CreateSilos(65);
        var calls = 0;
        var overlay = new DisseminationOverlayOptions
        {
            FanOutFactor = count =>
            {
                Assert.Equal(members.Length, count);
                calls++;
                return selectedFanout;
            },
        };
        var membershipFanout = Math.Clamp(selectedFanout, 1, members.Length);
        for (var index = 0; index < members.Length; index++)
        {
            var snapshot = new DisseminationMembershipSnapshot(new MembershipVersion(1), members[index], members, overlay);

            Assert.Equal(members.Skip(index * 8 + 1).Take(8), snapshot.AggregationChildren);
            Assert.Equal(members.Skip(membershipFanout * (index + 1)).Take(membershipFanout), snapshot.ForwardingTreeTargets);
            var originatorTargets = members.Take(membershipFanout).Where(silo => !silo.Equals(members[index]))
                .Concat(snapshot.ForwardingTreeTargets);
            Assert.Equal(originatorTargets, snapshot.OriginatorTreeTargets);
            Assert.Equal(snapshot.OriginatorTreeTargets.Length, snapshot.OriginatorTreeTargets.Distinct().Count());
        }

        Assert.Equal(members.Length, calls);
    }

    [Theory]
    [InlineData(1, int.MaxValue)]
    [InlineData(2, 8)]
    [InlineData(7, int.MaxValue)]
    [InlineData(17, 1)]
    [InlineData(17, 2)]
    public void AggregationTopologyClampsConfiguredFanOutToMembership(int memberCount, int fanout)
    {
        var members = CreateSilos(memberCount);
        var overlay = new DisseminationOverlayOptions { AggregationFanOutFactor = fanout };
        var effectiveFanout = Math.Min(memberCount, fanout);
        for (var index = 0; index < members.Length; index++)
        {
            var snapshot = new DisseminationMembershipSnapshot(new MembershipVersion(1), members[index], members, overlay);

            Assert.Equal(members.Skip(index * effectiveFanout + 1).Take(effectiveFanout), snapshot.AggregationChildren);
            Assert.DoesNotContain(members[index], snapshot.AggregationChildren);
            Assert.Equal(snapshot.AggregationChildren.Length, snapshot.AggregationChildren.Distinct().Count());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void NonMembersHaveNoAggregationNeighbors(int memberCount)
    {
        var local = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 30000), 1);
        var snapshot = new DisseminationMembershipSnapshot(
            new MembershipVersion(1),
            local,
            CreateSilos(memberCount),
            new DisseminationOverlayOptions { AggregationFanOutFactor = int.MaxValue });

        Assert.False(snapshot.IsAggregationRoot);
        Assert.Empty(snapshot.AggregationChildren);
        Assert.Empty(snapshot.GetOriginatorTargets(DisseminationRoutingMode.AggregationTree));
        Assert.Empty(snapshot.GetForwardingTargets(DisseminationRoutingMode.AggregationTree));
    }

    private static ValidateOptionsResult Validate(DisseminationOptions options) =>
        new DisseminationOptionsValidator().Validate(Options.DefaultName, options);

    private static void AssertValidationFailure(DisseminationOptions options, string message)
    {
        var result = Validate(options);
        Assert.True(result.Failed);
        Assert.NotNull(result.Failures);
        Assert.Equal(message, Assert.Single(result.Failures));
    }

    private static ImmutableArray<SiloAddress> CreateSilos(int count) =>
        Enumerable.Range(11111, count)
            .Select(port => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), port))
            .Order()
            .ToImmutableArray();
}
