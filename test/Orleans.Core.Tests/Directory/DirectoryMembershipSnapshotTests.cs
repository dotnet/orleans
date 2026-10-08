using System.Collections.Immutable;
using Documentation.Deployment;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.GrainDirectory;
using CsCheck;
using UnitTests.Directory;
using Xunit;

namespace NonSilo.Tests.Directory;

/// <summary>
/// Tests for directory membership snapshot functionality including range ownership and ring coverage validation.
/// </summary>
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
public sealed class DirectoryMembershipSnapshotTests
{
    private static readonly Gen<ClusterMembershipSnapshot> GenClusterMembershipSnapshot = Gen.Select(Gen.UInt, Gen.Enum<SiloStatus>(), (hash, status) => (hash, status))
        .Array[Gen.Int[1, 30]].Select((tuple) =>
    {
        var dict = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
        var port = 1;
        foreach (var item in tuple)
        {
            var (hash, status) = item;
            var addr = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port++), (int)hash);
            dict.Add(addr, new ClusterMember(addr, status, $"Silo_{hash}"));
        }

        return new ClusterMembershipSnapshot(dict.ToImmutable(), new(1));
    });

    private sealed record DirectoryMembershipSnapshotTestCase(DirectoryMembershipSnapshot Snapshot, uint[][] HashesByMember);

    private static readonly Gen<DirectoryMembershipSnapshotTestCase> GenDirectoryMembershipSnapshotTestCase =
        GenClusterMembershipSnapshot.SelectMany(snapshot =>
        {
            var activeMemberCount = snapshot.Members.Count(static member => member.Value.Status == SiloStatus.Active);
            return Gen.Int[1, GrainDirectoryOptions.DEFAULT_PARTITIONS_PER_SILO * 2].SelectMany(partitionCount =>
                Gen.UInt.Array[partitionCount].Array[activeMemberCount].Select(hashes =>
                {
                    var i = 0;
                    return new DirectoryMembershipSnapshotTestCase(
                        new DirectoryMembershipSnapshot(snapshot, null!, partitionCount, (_, _) => hashes[i++].ToImmutableArray()),
                        hashes);
                }));
        });

    private static readonly Gen<DirectoryMembershipSnapshot> GenDirectoryMembershipSnapshot =
        GenDirectoryMembershipSnapshotTestCase.Select(static testCase => testCase.Snapshot);

    [Fact]
    public void GetOwnerTest()
    {
        // As long as the cluster has at least one member, we should be able to find an owner.
        Gen.Select(GenDirectoryMembershipSnapshot, Gen.UInt)
            .Sample((snapshot, hash) => Assert.Equal(snapshot.Members.Length > 0, snapshot.TryGetOwner(hash, out var owner, out _)));
    }

    [Fact]
    public void MembersDoNotIntersectTest()
    {
        // Member ranges should not intersect.
        GenDirectoryMembershipSnapshot.Where(s => s.Members.Length > 0)
            .Sample(snapshot =>
            {
                foreach (var range in snapshot.RangeOwners)
                {
                    foreach (var otherRange in snapshot.RangeOwners)
                    {
                        if (range == otherRange)
                        {
                            continue;
                        }

                        Assert.False(range.Range.Intersects(otherRange.Range));
                    }
                }
            });
    }

    [Fact]
    public void GetRangeReturnsRangeForRequestedPartition()
    {
        GenDirectoryMembershipSnapshotTestCase.Where(testCase => testCase.Snapshot.Members.Length > 0)
            .Sample(testCase =>
            {
                var snapshot = testCase.Snapshot;

                for (var memberIndex = 0; memberIndex < snapshot.Members.Length; memberIndex++)
                {
                    var member = snapshot.Members[memberIndex];
                    for (var partitionIndex = 0; partitionIndex < snapshot.PartitionCount; partitionIndex++)
                    {
                        var expectedRange = GetExpectedRange(testCase.HashesByMember, memberIndex, partitionIndex);
                        Assert.Equal(expectedRange, snapshot.GetRange(member, partitionIndex));
                    }
                }
            });
    }

    [Fact]
    public void GetRangeReturnsEmptyForPartitionMissingFromSnapshot()
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1), 1);

        Assert.Equal(RingRange.Empty, DirectoryMembershipSnapshot.Default.GetRange(member, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(30)]
    public void DefaultPartitionBoundariesPreserveCurrentMapping(int partitionCount)
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11111), 1);
        var options = new GrainDirectoryOptions();
        var expected = partitionCount == 1
            ? [unchecked((uint)member.GetConsistentHashCode())]
            : member.GetUniformHashCodes(partitionCount);

        Assert.Equal(expected, options.GetPartitionBoundaries(member, partitionCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    public void UniformHashCodesAreImmutableAndReuseCachedStorage(int count)
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11200 + count), 1);
        var hashes = member.GetUniformHashCodes(count);

        Assert.False(hashes.IsDefault);
        Assert.Equal(count, hashes.Length);
        Assert.True(hashes.Equals(member.GetUniformHashCodes(count)));
        Assert.True(((ICollection<uint>)hashes).IsReadOnly);
        if (count > 0)
        {
            var originalHash = hashes[0];
            var copy = hashes.ToArray();
            copy[0] ^= uint.MaxValue;

            Assert.Throws<NotSupportedException>(() => ((IList<uint>)hashes)[0] = 0);
            Assert.Equal(originalHash, member.GetUniformHashCodes(count)[0]);
        }
    }

    [Fact]
    public void UniformHashCodesRemainStableWhenCacheCountChanges()
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11231), 1);
        var hashes = member.GetUniformHashCodes(30);
        var expected = hashes.ToArray();

        Assert.Single(member.GetUniformHashCodes(1));

        Assert.Equal(expected, hashes);
        Assert.Equal(expected, member.GetUniformHashCodes(30));
    }

    [Fact]
    public void LegacyPartitionBoundariesPreserveCachedHashOrder()
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11112), 1);
        ImmutableArray<uint> generated = [uint.MaxValue, 100, 0x80000000, 200];
        member.InternalSetUniformHashCodes(generated);

        var options = GetDocumentedLegacyOptions();
        var boundaries = options.GetPartitionBoundaries(member, generated.Length);

        Assert.Equal([100u, 200u, 0x80000000u, uint.MaxValue], boundaries);
        Assert.Equal([uint.MaxValue, 100u, 0x80000000u, 200u], member.GetUniformHashCodes(generated.Length));
        Assert.False(generated.Equals(boundaries));
    }

    [Fact]
    public void LegacyPartitionBoundariesMatchOrleans10_1PartitionRanges()
    {
        const int partitionCount = 30;
        var members = Enumerable.Range(0, 3)
            .Select(index => SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11120 + index), 1))
            .Order()
            .ToArray();
        var membership = new ClusterMembershipSnapshot(
            members.ToImmutableDictionary(member => member, member => new ClusterMember(member, SiloStatus.Active, member.ToString())),
            new(1));
        var options = GetDocumentedLegacyOptions();
        Assert.Equal(partitionCount, options.PartitionsPerSilo);
        var snapshot = new DirectoryMembershipSnapshot(membership, null!, options.PartitionsPerSilo, options.GetPartitionBoundaries);
        var legacyHashes = members.Select(member => member.GetUniformHashCodes(partitionCount).Order().ToArray()).ToArray();

        Assert.Equal(members, snapshot.Members);
        Assert.Equal(members.Length * partitionCount, snapshot.RangeOwners.Count);
        for (var memberIndex = 0; memberIndex < members.Length; memberIndex++)
        {
            for (var partitionIndex = 0; partitionIndex < partitionCount; partitionIndex++)
            {
                var expectedRange = GetExpectedRange(legacyHashes, memberIndex, partitionIndex);
                Assert.Equal(expectedRange, snapshot.GetRange(members[memberIndex], partitionIndex));
                Assert.True(snapshot.TryGetOwner(expectedRange.End, out var owner, out _));
                Assert.Equal(members[memberIndex], owner);
                Assert.Contains((expectedRange, memberIndex, partitionIndex), snapshot.RangeOwners);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void PartitionBoundariesRejectIncorrectCount(int returnedCount)
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11113), 1);
        var membership = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(member, new ClusterMember(member, SiloStatus.Active, "Silo")),
            new(1));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new DirectoryMembershipSnapshot(membership, null!, 3, (_, _) => new uint[returnedCount].ToImmutableArray()));

        Assert.Contains("exactly 3 boundaries", exception.Message);
        Assert.Contains(member.ToString(), exception.Message);
    }

    [Fact]
    public void PartitionBoundariesRejectDefaultResult()
    {
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11114), 1);
        var membership = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(member, new ClusterMember(member, SiloStatus.Active, "Silo")),
            new(1));

        Assert.Throws<InvalidOperationException>(() => new DirectoryMembershipSnapshot(membership, null!, 3, (_, _) => default));
    }

    [Fact]
    public void PartitionBoundariesRejectNullFunction()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DirectoryMembershipService(null!, null!, null!, 3, null!));
    }

    private static GrainDirectoryOptions GetDocumentedLegacyOptions()
    {
        var services = new ServiceCollection();
        var siloBuilder = Substitute.For<ISiloBuilder>();
        siloBuilder.Services.Returns(services);
        DirectoryPartitioningSnippet.Configure(siloBuilder);
        using var serviceProvider = services.BuildServiceProvider();
        return serviceProvider.GetRequiredService<IOptions<GrainDirectoryOptions>>().Value;
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task BoundaryFailureStopsProcessingAndFaultsMembershipReaders(int returnedCount)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11115), 1);
        var membership = new MockClusterMembershipService();
        var clusterMembershipService = Substitute.For<IClusterMembershipService>();
        clusterMembershipService.MembershipUpdates.Returns(membership.Target.MembershipUpdates);
        clusterMembershipService.Refresh(Arg.Any<MembershipVersion>(), Arg.Any<CancellationToken>())
            .Returns(call => membership.Target.Refresh(call.ArgAt<MembershipVersion>(0), call.ArgAt<CancellationToken>(1)));
        var logger = Substitute.For<ILogger<DirectoryMembershipService>>();
        logger.IsEnabled(LogLevel.Error).Returns(true);
        var boundaryCalls = 0;
        var callbackFailure = new InvalidOperationException("The partition boundary callback failed.");

        await using var directoryMembership = new DirectoryMembershipService(
            clusterMembershipService,
            null!,
            logger,
            3,
            (_, _) =>
            {
                if (Interlocked.Increment(ref boundaryCalls) > 1)
                {
                    // Bound a regression which resubscribes to the same invalid snapshot.
                    timeout.Cancel();
                }

                if (returnedCount == -2)
                {
                    throw callbackFailure;
                }

                return returnedCount == -1 ? default : new uint[returnedCount].ToImmutableArray();
            });
        var initialView = await directoryMembership.RefreshViewAsync(membership.CurrentVersion, timeout.Token);
        Assert.Equal(membership.CurrentVersion, initialView.Version);
        Assert.Equal(0, boundaryCalls);

        await using var updates = directoryMembership.ViewUpdates.GetAsyncEnumerator(timeout.Token);
        Assert.True(await updates.MoveNextAsync());
        var nextUpdate = updates.MoveNextAsync().AsTask();
        var nextVersion = new MembershipVersion(membership.CurrentVersion.Value + 1);
        var refresh = directoryMembership.RefreshViewAsync(nextVersion, timeout.Token).AsTask();
        Assert.False(nextUpdate.IsCompleted);
        Assert.False(refresh.IsCompleted);

        membership.UpdateSiloStatus(member, SiloStatus.Active, "Silo");

        var failure = await Assert.ThrowsAsync<OrleansConfigurationException>(() => nextUpdate);
        Assert.Same(failure, await Assert.ThrowsAsync<OrleansConfigurationException>(() => refresh));
        Assert.Same(failure, Assert.Throws<OrleansConfigurationException>(() => directoryMembership.CurrentView));
        Assert.Same(failure, await Assert.ThrowsAsync<OrleansConfigurationException>(() =>
            directoryMembership.RefreshViewAsync(nextVersion, timeout.Token).AsTask()));
        await using var futureUpdates = directoryMembership.ViewUpdates.GetAsyncEnumerator(timeout.Token);
        Assert.Same(failure, await Assert.ThrowsAsync<OrleansConfigurationException>(() => futureUpdates.MoveNextAsync().AsTask()));
        if (returnedCount == -2)
        {
            Assert.Same(callbackFailure, failure.InnerException);
        }
        else
        {
            Assert.Contains("exactly 3 boundaries", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
        }

        await directoryMembership.DisposeAsync();
        Assert.Equal(1, boundaryCalls);
        _ = clusterMembershipService.Received(1).MembershipUpdates;
        var log = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger<DirectoryMembershipService>.Log));
        Assert.Equal(LogLevel.Error, log.GetArguments()[0]);
        Assert.Same(failure, log.GetArguments()[3]);
    }

    [Fact]
    public async Task MembershipStreamFailureCanResubscribe()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var member = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11116), 1);
        var membership = new MockClusterMembershipService(new() { [member] = (SiloStatus.Active, "Silo") });
        var streamFailure = new InvalidOperationException("The membership subscription failed.");
        var failedEnumerator = Substitute.For<IAsyncEnumerator<ClusterMembershipSnapshot>>();
        failedEnumerator.MoveNextAsync().Returns(ValueTask.FromException<bool>(streamFailure));
        var failedUpdates = Substitute.For<IAsyncEnumerable<ClusterMembershipSnapshot>>();
        failedUpdates.GetAsyncEnumerator(Arg.Any<CancellationToken>()).Returns(failedEnumerator);
        var clusterMembershipService = Substitute.For<IClusterMembershipService>();
        clusterMembershipService.MembershipUpdates.Returns(failedUpdates, membership.Target.MembershipUpdates);
        var logger = Substitute.For<ILogger<DirectoryMembershipService>>();
        logger.IsEnabled(LogLevel.Error).Returns(true);

        await using var directoryMembership = new DirectoryMembershipService(
            clusterMembershipService, null!, logger, 3, DirectoryMembershipSnapshot.DefaultGetRingBoundaries);
        var view = await directoryMembership.RefreshViewAsync(membership.CurrentVersion, timeout.Token);

        Assert.Equal(membership.CurrentVersion, view.Version);
        Assert.Equal(member, Assert.Single(view.Members));
        _ = clusterMembershipService.Received(2).MembershipUpdates;
        var log = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger<DirectoryMembershipService>.Log));
        Assert.Same(streamFailure, log.GetArguments()[3]);
    }

    [Fact]
    public async Task DisposeCompletesMembershipReadersNormally()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var membership = new MockClusterMembershipService();
        await using var directoryMembership = new DirectoryMembershipService(
            membership.Target, null!, NullLogger<DirectoryMembershipService>.Instance, 3, DirectoryMembershipSnapshot.DefaultGetRingBoundaries);
        await directoryMembership.RefreshViewAsync(membership.CurrentVersion, timeout.Token);
        await using var updates = directoryMembership.ViewUpdates.GetAsyncEnumerator(timeout.Token);
        Assert.True(await updates.MoveNextAsync());
        var nextUpdate = updates.MoveNextAsync().AsTask();
        Assert.False(nextUpdate.IsCompleted);

        await directoryMembership.DisposeAsync();

        Assert.False(await nextUpdate);
    }

    private static RingRange GetExpectedRange(uint[][] hashesByMember, int memberIndex, int partitionIndex)
    {
        var boundaries = new List<(uint Hash, int MemberIndex, int PartitionIndex)>();
        for (var i = 0; i < hashesByMember.Length; i++)
        {
            var hashes = hashesByMember[i];
            for (var j = 0; j < hashes.Length; j++)
            {
                boundaries.Add((hashes[j], i, j));
            }
        }

        boundaries.Sort(static (left, right) =>
        {
            var hashCompare = left.Hash.CompareTo(right.Hash);
            if (hashCompare != 0)
            {
                return hashCompare;
            }

            var partitionCompare = left.PartitionIndex.CompareTo(right.PartitionIndex);
            if (partitionCompare != 0)
            {
                return partitionCompare;
            }

            return left.MemberIndex.CompareTo(right.MemberIndex);
        });

        for (var i = 1; i < boundaries.Count;)
        {
            if (boundaries[i].Hash == boundaries[i - 1].Hash)
            {
                boundaries.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }

        var boundaryIndex = boundaries.FindIndex(boundary => boundary.MemberIndex == memberIndex && boundary.PartitionIndex == partitionIndex);
        if (boundaryIndex < 0)
        {
            return RingRange.Empty;
        }

        if (boundaries.Count == 1)
        {
            return RingRange.Full;
        }

        var current = boundaries[boundaryIndex];
        var next = boundaries[(boundaryIndex + 1) % boundaries.Count];
        return RingRange.Create(current.Hash, next.Hash);
    }

    [Fact]
    public void ViewCoversRingTest()
    {
        // The union of all member ranges should cover the entire ring.
        GenDirectoryMembershipSnapshot.Where(s => s.Members.Length > 0)
            .Sample(snapshot =>
            {
                ulong sum = 0;
                var allRanges = new List<RingRange>();
                foreach (var member in snapshot.Members)
                {
                    Assert.Equal(
                        snapshot.GetMemberRanges(member).Aggregate(0UL, static (sum, range) => sum + range.Size),
                        snapshot.GetMemberRangesByPartition(member).Aggregate(0UL, static (sum, range) => sum + range.Size));
                    foreach (var range in snapshot.GetMemberRanges(member))
                    {
                        allRanges.Add(range);
                        sum += range.Size;
                    }
                }

                Assert.Equal(1UL << 32, sum);

                var allRangesCollection = RingRangeCollection.Create(allRanges);

                Assert.Equal(1UL << 32, allRangesCollection.Size);
                Assert.Equal(100f, allRangesCollection.SizePercent);
                Assert.False(allRangesCollection.IsEmpty);
                Assert.False(allRangesCollection.IsDefault);
                Assert.True(allRangesCollection.IsFull);
            });
    }

    [Fact]
    public void MemberRangesCoverRingTest()
    {
        // The union of all member ranges should cover the entire ring.
        GenDirectoryMembershipSnapshot.Where(s => s.Members.Length > 0)
            .Sample(snapshot =>
            {
                ulong sum = 0;
                var allRanges = new List<RingRange>();
                foreach (var member in snapshot.Members)
                {
                    foreach (var range in snapshot.GetMemberRangesByPartition(member))
                    {
                        allRanges.Add(range);
                        sum += range.Size;
                    }
                }

                Assert.Equal(1UL << 32, sum);
                var allRangesCollection = RingRangeCollection.Create(allRanges);
                Assert.Equal(1UL << 32, allRangesCollection.Size);
                Assert.Equal(100f, allRangesCollection.SizePercent);
                Assert.False(allRangesCollection.IsEmpty);
                Assert.False(allRangesCollection.IsDefault);
                Assert.True(allRangesCollection.IsFull);
            });
    }
}
