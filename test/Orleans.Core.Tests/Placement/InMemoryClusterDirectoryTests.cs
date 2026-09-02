using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using TestExtensions;
using Xunit;

namespace UnitTests.Placement;

[TestArea("Placement")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "3")]
[Trait("FullyQualifiedName", "UnitTests.Placement.InMemoryClusterDirectoryTests")]
public sealed class InMemoryClusterDirectoryTests
{
    private static readonly GrainId GrainId = GrainId.Create("directory.test", "grain-1");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Start = new(2035, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task Lookup_MissingGrain_ReturnsNoEntry()
    {
        var (_, directory) = CreateDirectory();

        var result = await directory.Lookup(GrainId);

        Assert.Null(result);
    }

    [Fact]
    public async Task Create_MissingGrain_CreatesOwnerVersionFenceAndLease()
    {
        var (clock, directory) = CreateDirectory();

        var entry = await directory.GetOrCreate(GrainId, "east", 7, Lease);

        AssertEntry(entry, "east", version: 1, epoch: 7, fence: 1, Start + Lease);
        Assert.Equal(entry, await directory.Lookup(GrainId));
        Assert.Equal(Start, clock.GetUtcNow());
    }

    [Fact]
    public async Task Create_ExistingLiveEntry_ReturnsConflictWithoutMutation()
    {
        var (_, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 3, Lease);

        var observed = await directory.GetOrCreate(GrainId, "west", 4, Lease);

        Assert.Same(original, observed);
        AssertEntry(observed, "east", 1, 3, 1, Start + Lease);
    }

    [Fact]
    public async Task Create_ExpiredEntry_ReacquiresWithHigherVersionAndFence()
    {
        var (clock, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 3, Lease);
        clock.Advance(Lease);

        var replacement = await directory.GetOrCreate(GrainId, "west", 4, Lease);

        AssertEntry(replacement, "west", 2, 4, 2, Start + Lease + Lease);
        Assert.True(replacement.Version > original.Version);
        Assert.True(replacement.FencingToken > original.FencingToken);
    }

    [Fact]
    public async Task Create_ExpiredEntry_ReacquiresAtSameTopologyEpoch()
    {
        var (clock, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 3, Lease);
        clock.Advance(Lease);

        var replacement = await directory.GetOrCreate(GrainId, "west", 3, Lease);

        AssertEntry(replacement, "west", 2, 3, 2, Start + Lease + Lease);
        Assert.Equal(original.Version + 1, replacement.Version);
        Assert.Equal(original.FencingToken + 1, replacement.FencingToken);
    }

    [Fact]
    public async Task Renew_CurrentOwner_ExtendsLeaseWithoutChangingFence()
    {
        var (clock, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 8, Lease);
        clock.Advance(TimeSpan.FromMinutes(2));

        var renewed = await directory.TryRenew(GrainId, original.Version, "east", Lease);

        Assert.NotNull(renewed);
        AssertEntry(renewed, "east", original.Version, original.TopologyEpoch, original.FencingToken, Start + TimeSpan.FromMinutes(7));
        Assert.NotEqual(original.LeaseExpiration, renewed.LeaseExpiration);
    }

    [Fact]
    public async Task Renew_AtLeaseBoundary_FailsAndDoesNotResurrect()
    {
        var (clock, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 1, Lease);
        clock.Advance(Lease);

        var renewed = await directory.TryRenew(GrainId, original.Version, original.ClusterId, Lease);

        Assert.Null(renewed);
        Assert.Null(await directory.Lookup(GrainId));
    }

    [Fact]
    public async Task Renew_StaleOwnerVersionEpochOrFence_FailsWithoutMutation()
    {
        var (clock, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 2, Lease);
        clock.Advance(Lease);
        var current = await directory.TryMove(GrainId, original.Version, "west", 3, Lease);
        Assert.NotNull(current);

        var staleOwner = await directory.TryRenew(GrainId, original.Version, original.ClusterId, Lease);
        var staleVersion = await directory.TryRenew(GrainId, original.Version, current.ClusterId, Lease);

        Assert.Null(staleOwner);
        Assert.Null(staleVersion);
        Assert.Equal(current, await directory.Lookup(GrainId));
        Assert.True(current.TopologyEpoch > original.TopologyEpoch);
        Assert.True(current.FencingToken > original.FencingToken);
    }

    [Fact]
    public async Task Move_LiveOwner_FailsWithoutMutation()
    {
        var (_, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 10, Lease);

        var moved = await directory.TryMove(GrainId, original.Version, "west", 11, Lease);

        Assert.Null(moved);
        Assert.Equal(original, await directory.Lookup(GrainId));
    }

    [Fact]
    public async Task Move_ExpiredOwner_ChangesClusterAndIncrementsVersionAndFence()
    {
        var (clock, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 10, Lease);
        clock.Advance(Lease);

        var moved = await directory.TryMove(GrainId, original.Version, "west", 11, Lease);

        Assert.NotNull(moved);
        AssertEntry(moved, "west", 2, 11, 2, Start + Lease + Lease);
        Assert.Equal(moved, await directory.Lookup(GrainId));
    }

    [Fact]
    public async Task Move_ToSameOwner_HasDefinedIdempotentResult()
    {
        var (clock, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 4, Lease);
        clock.Advance(Lease);

        var moved = await directory.TryMove(GrainId, original.Version, "east", 4, Lease);

        Assert.NotNull(moved);
        Assert.Equal("east", moved.ClusterId);
        Assert.Equal(original.Version + 1, moved.Version);
        Assert.Equal(original.FencingToken + 1, moved.FencingToken);
    }

    [Fact]
    public async Task Move_StaleSourceOwnerVersionEpochOrFence_FailsWithoutMutation()
    {
        var (clock, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 5, Lease);
        clock.Advance(Lease);
        var current = await directory.TryMove(GrainId, original.Version, "west", 6, Lease);
        Assert.NotNull(current);

        var replay = await directory.TryMove(GrainId, original.Version, "north", original.TopologyEpoch, Lease);

        Assert.Null(replay);
        Assert.Equal(current, await directory.Lookup(GrainId));
    }

    [Fact]
    public async Task Move_ExpiredEntry_FailsOrReacquiresOnlyThroughCreateContract()
    {
        var (clock, directory) = CreateDirectory();
        var expired = await directory.GetOrCreate(GrainId, "east", 1, Lease);
        clock.Advance(Lease);
        var reacquired = await directory.GetOrCreate(GrainId, "west", 2, Lease);

        var staleMove = await directory.TryMove(GrainId, expired.Version, "north", 3, Lease);

        Assert.Null(staleMove);
        Assert.Equal(reacquired, await directory.Lookup(GrainId));
        Assert.True(reacquired.FencingToken > expired.FencingToken);
    }

    [Fact]
    public async Task Validate_CurrentUnexpiredEntry_Succeeds()
    {
        var (_, directory) = CreateDirectory();
        var entry = await directory.GetOrCreate(GrainId, "east", 2, Lease);

        var observed = await directory.Lookup(GrainId);

        Assert.Equal(entry, observed);
        Assert.True(IsSameOwnership(entry, observed));
    }

    [Fact]
    public async Task Validate_ExpiredOrStaleEntry_Fails()
    {
        var (clock, directory) = CreateDirectory();
        var stale = await directory.GetOrCreate(GrainId, "east", 2, Lease);
        clock.Advance(Lease);

        var expiredObservation = await directory.Lookup(GrainId);
        var current = await directory.GetOrCreate(GrainId, "west", 3, Lease);

        Assert.Null(expiredObservation);
        Assert.False(IsSameOwnership(stale, current));
    }

    [Fact]
    public async Task TopologyEpochChange_InvalidatesStaleMutation()
    {
        var (clock, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 10, Lease);
        clock.Advance(Lease);
        var current = await directory.TryMove(GrainId, original.Version, "west", 12, Lease);
        Assert.NotNull(current);
        clock.Advance(Lease);

        var staleEpochMove = await directory.TryMove(GrainId, current.Version, "north", 11, Lease);

        Assert.Null(staleEpochMove);
        Assert.Null(await directory.Lookup(GrainId));
        var replacement = await directory.GetOrCreate(GrainId, "north", 13, Lease);
        Assert.True(replacement.Version > current.Version);
        Assert.Equal(13, replacement.TopologyEpoch);
    }

    [Fact]
    public async Task ConcurrentCreate_HasExactlyOneWinner()
    {
        var (_, directory) = CreateDirectory();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = 0;
        var contenders = Enumerable.Range(0, 8).Select(async index =>
        {
            if (Interlocked.Increment(ref waiting) == 8)
            {
                ready.SetResult();
            }

            await start.Task;
            return await directory.GetOrCreate(GrainId, $"cluster-{index}", 1, Lease);
        }).ToArray();
        await ready.Task;

        start.SetResult();
        var entries = await Task.WhenAll(contenders);

        var winner = Assert.Single(entries.Distinct());
        Assert.All(entries, entry => Assert.Same(winner, entry));
        Assert.True(winner.Version > 0);
        Assert.Equal(winner.Version, winner.FencingToken);
    }

    [Fact]
    public async Task ConcurrentRenewAndMove_LiveLeaseOnlyAllowsRenewal()
    {
        var (_, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "east", 1, Lease);
        var renewGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewAttempt = Task.Run(async () =>
        {
            await renewGate.Task;
            return await directory.TryRenew(GrainId, original.Version, "east", Lease);
        });
        var moveAttempt = Task.Run(async () =>
        {
            await moveGate.Task;
            return await directory.TryMove(GrainId, original.Version, "west", 2, Lease);
        });

        moveGate.SetResult();
        var moved = await moveAttempt;
        renewGate.SetResult();
        var renewed = await renewAttempt;

        Assert.Null(moved);
        Assert.NotNull(renewed);
        Assert.Equal(renewed, await directory.Lookup(GrainId));
    }

    [Fact]
    public async Task NamedDirectoryInstances_IsolateState()
    {
        var clock = new FakeTimeProvider(Start);
        var first = new InMemoryClusterDirectory(clock);
        var second = new InMemoryClusterDirectory(clock);

        var entry = await first.GetOrCreate(GrainId, "east", 1, Lease);

        Assert.Equal(entry, await first.Lookup(GrainId));
        Assert.Null(await second.Lookup(GrainId));
    }

    [Fact]
    public async Task EveryOperation_ObservesPreCanceledTokenWithoutMutation()
    {
        var (_, directory) = CreateDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.Lookup(GrainId, cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.GetOrCreate(GrainId, "east", 1, Lease, cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.TryRenew(GrainId, 1, "east", Lease, cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.TryMove(GrainId, 1, "west", 2, Lease, cancellation.Token).AsTask());
        Assert.Null(await directory.Lookup(GrainId));
    }

    [Fact]
    public void CsCheck_DirectoryFencingToken_IsStrictlyMonotonicAcrossCommittedOwnershipChanges()
    {
        Gen.Int.Array[8].Sample(
            choices => VerifyMonotonicFences(choices).GetAwaiter().GetResult(),
            seed: "0N0XIzNsQ0P3FENCE",
            iter: 80,
            threads: 1,
            print: static choices => $"ownership-choices=[{string.Join(',', choices)}]");
    }

    [Fact]
    public void CsCheck_DirectoryOperationSequence_MatchesReferenceModel()
    {
        Gen.Int.Array[12].Sample(
            operations => VerifyReferenceSequence(operations).GetAwaiter().GetResult(),
            seed: "0N0XIzNsQ0P3MODEL",
            iter: 80,
            threads: 1,
            print: static operations => $"operations=[{string.Join(',', operations)}]");
    }

    private static async Task VerifyMonotonicFences(int[] choices)
    {
        var (clock, directory) = CreateDirectory();
        ClusterDirectoryEntry current = await directory.GetOrCreate(GrainId, "cluster-0", 1, Lease);
        var fences = new List<long> { current.FencingToken };
        for (var index = 0; index < choices.Length; index++)
        {
            if ((choices[index] & 1) == 0)
            {
                clock.Advance(Lease);
                current = (await directory.TryMove(
                    GrainId,
                    current.Version,
                    $"cluster-{index + 1}",
                    index + 2,
                    Lease))!;
            }
            else
            {
                clock.Advance(Lease);
                current = await directory.GetOrCreate(GrainId, $"cluster-{index + 1}", index + 2, Lease);
            }

            fences.Add(current.FencingToken);
        }

        Assert.Equal(fences.Count, fences.Distinct().Count());
        Assert.All(fences.Zip(fences.Skip(1)), pair => Assert.True(
            pair.First < pair.Second,
            $"fences=[{string.Join(',', fences)}]"));
    }

    private static async Task VerifyReferenceSequence(int[] operations)
    {
        var (clock, directory) = CreateDirectory();
        ClusterDirectoryEntry? model = null;
        long nextVersion = 0;
        var now = Start;
        for (var index = 0; index < operations.Length; index++)
        {
            switch ((uint)operations[index] % 4)
            {
                case 0:
                    var acquired = await directory.GetOrCreate(GrainId, $"cluster-{index % 3}", index, Lease);
                    if (model is null || model.LeaseExpiration <= now)
                    {
                        nextVersion++;
                        model = new ClusterDirectoryEntry(GrainId, $"cluster-{index % 3}", nextVersion, index, nextVersion, now + Lease);
                    }

                    Assert.Equal(model, acquired);
                    break;
                case 1 when model is not null:
                    var renewed = await directory.TryRenew(GrainId, model.Version, model.ClusterId, Lease);
                    if (model.LeaseExpiration > now)
                    {
                        model = new ClusterDirectoryEntry(GrainId, model.ClusterId, model.Version, model.TopologyEpoch, model.FencingToken, now + Lease);
                        Assert.Equal(model, renewed);
                    }
                    else
                    {
                        Assert.Null(renewed);
                    }

                    break;
                case 2 when model is not null:
                    var moved = await directory.TryMove(GrainId, model.Version, $"cluster-{(index + 1) % 3}", Math.Max(index, model.TopologyEpoch), Lease);
                    if (model.LeaseExpiration <= now)
                    {
                        nextVersion++;
                        model = new ClusterDirectoryEntry(GrainId, $"cluster-{(index + 1) % 3}", nextVersion, Math.Max(index, model.TopologyEpoch), nextVersion, now + Lease);
                        Assert.Equal(model, moved);
                    }
                    else
                    {
                        Assert.Null(moved);
                    }

                    break;
                default:
                    clock.Advance(Lease);
                    now += Lease;
                    Assert.Null(await directory.Lookup(GrainId));
                    break;
            }

            Assert.Equal(model is { LeaseExpiration: var expiration } && expiration > now ? model : null, await directory.Lookup(GrainId));
        }
    }

    private static (FakeTimeProvider Clock, InMemoryClusterDirectory Directory) CreateDirectory()
    {
        var clock = new FakeTimeProvider(Start);
        return (clock, new InMemoryClusterDirectory(clock));
    }

    private static bool IsSameOwnership(ClusterDirectoryEntry expected, ClusterDirectoryEntry? actual)
        => actual is not null
            && expected.GrainId == actual.GrainId
            && expected.ClusterId == actual.ClusterId
            && expected.Version == actual.Version
            && expected.TopologyEpoch == actual.TopologyEpoch
            && expected.FencingToken == actual.FencingToken;

    private static void AssertEntry(
        ClusterDirectoryEntry entry,
        string clusterId,
        long version,
        long epoch,
        long fence,
        DateTimeOffset expiration)
    {
        Assert.Equal(GrainId, entry.GrainId);
        Assert.Equal(clusterId, entry.ClusterId);
        Assert.Equal(version, entry.Version);
        Assert.Equal(epoch, entry.TopologyEpoch);
        Assert.Equal(fence, entry.FencingToken);
        Assert.Equal(expiration, entry.LeaseExpiration);
    }

    [Fact]
    public async Task ConcurrentExpiryReacquisition_HasExactlyOneLiveOwnerAndRejectsStaleVersion()
    {
        var (clock, directory) = CreateDirectory();
        var original = await directory.GetOrCreate(GrainId, "original", 1, Lease);
        clock.Advance(Lease);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = 0;
        var contenders = Enumerable.Range(0, 12).Select(async index =>
        {
            if (Interlocked.Increment(ref waiting) == 12)
            {
                ready.SetResult();
            }

            await start.Task;
            return index % 2 == 0
                ? await directory.GetOrCreate(GrainId, $"acquirer-{index}", 2, Lease)
                : await directory.TryMove(GrainId, original.Version, $"mover-{index}", 2, Lease);
        }).ToArray();
        await ready.Task;

        start.SetResult();
        var results = await Task.WhenAll(contenders);
        var current = await directory.Lookup(GrainId);

        Assert.NotNull(current);
        Assert.True(current.Version > original.Version);
        Assert.True(current.FencingToken > original.FencingToken);
        Assert.Equal(current.Version, current.FencingToken);
        Assert.Equal(Start + Lease + Lease, current.LeaseExpiration);
        Assert.Single(results.Where(static result => result is not null).Distinct());
        Assert.All(results, result => Assert.True(result is null || result == current));
        Assert.Null(await directory.TryRenew(GrainId, original.Version, original.ClusterId, Lease));
        Assert.Null(await directory.TryMove(GrainId, current.Version, "live-relocation", 3, Lease));
        Assert.Equal(current, await directory.Lookup(GrainId));
    }

    [Fact]
    public void CsCheck_DirectoryReferenceModel_CoversRenewExpiryReacquireAndStaleVersion()
    {
        Gen.Int.Array[18].Sample(
            operations => VerifyExtendedReferenceSequence(operations).GetAwaiter().GetResult(),
            seed: "0N0XIzNsQ0P3REFERENCE",
            iter: 60,
            threads: 1,
            print: static operations => $"operations=[{string.Join(',', operations)}]");
    }

    [Fact]
    public void CsCheck_ConcurrentReacquisition_PreservesExactlyOneOwnerAndStrictFences()
    {
        Gen.Int.Array[6].Sample(
            choices => VerifyConcurrentReacquisition(choices).GetAwaiter().GetResult(),
            seed: "0N0XIzNsQ0P3RACES",
            iter: 40,
            threads: 1,
            print: static choices => $"contender-choices=[{string.Join(',', choices)}]");
    }

    private static async Task VerifyExtendedReferenceSequence(int[] operations)
    {
        var (clock, directory) = CreateDirectory();
        ReferenceOwnership? model = null;
        var now = Start;
        long nextVersion = 0;
        for (var index = 0; index < operations.Length; index++)
        {
            var operation = (uint)operations[index] % 5;
            switch (operation)
            {
                case 0:
                    AssertReference(model, now, await directory.Lookup(GrainId));
                    break;
                case 1:
                    var cluster = $"cluster-{(uint)operations[index] % 4}";
                    var acquired = await directory.GetOrCreate(GrainId, cluster, index + 1, Lease);
                    if (model is null || model.LeaseExpiration <= now)
                    {
                        nextVersion++;
                        model = new ReferenceOwnership(
                            cluster,
                            nextVersion,
                            index + 1,
                            nextVersion,
                            now + Lease);
                    }

                    AssertReference(model, now, acquired);
                    break;
                case 2 when model is not null:
                    var renewed = await directory.TryRenew(
                        GrainId,
                        model.Version,
                        model.ClusterId,
                        Lease);
                    if (model.LeaseExpiration > now)
                    {
                        model = model with { LeaseExpiration = now + Lease };
                        AssertReference(model, now, renewed);
                    }
                    else
                    {
                        Assert.Null(renewed);
                    }

                    break;
                case 3 when model is not null:
                    var before = await directory.Lookup(GrainId);
                    var staleVersion = model.Version == 1 ? 2 : model.Version - 1;
                    Assert.Null(await directory.TryRenew(
                        GrainId,
                        staleVersion,
                        model.ClusterId,
                        Lease));
                    Assert.Equal(before, await directory.Lookup(GrainId));
                    break;
                default:
                    clock.Advance(Lease);
                    now += Lease;
                    Assert.Null(await directory.Lookup(GrainId));
                    break;
            }

            AssertReference(model, now, await directory.Lookup(GrainId));
        }
    }

    private static async Task VerifyConcurrentReacquisition(int[] choices)
    {
        var (clock, directory) = CreateDirectory();
        var current = await directory.GetOrCreate(GrainId, "initial", 1, Lease);
        foreach (var (choice, generation) in choices.Select((choice, index) => (choice, index + 1)))
        {
            clock.Advance(Lease);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var contenders = Enumerable.Range(0, 4).Select(async contender =>
            {
                await start.Task;
                var cluster = $"cluster-{generation}-{((uint)(choice + contender) % 5)}";
                return await directory.GetOrCreate(GrainId, cluster, generation + 1, Lease);
            }).ToArray();

            start.SetResult();
            var entries = await Task.WhenAll(contenders);
            var winner = Assert.Single(entries.Distinct());
            Assert.All(entries, entry => Assert.Equal(winner, entry));
            Assert.True(winner.Version > current.Version);
            Assert.True(winner.FencingToken > current.FencingToken);
            Assert.Equal(winner.Version, winner.FencingToken);
            Assert.Equal(winner, await directory.Lookup(GrainId));
            current = winner;
        }
    }

    private static void AssertReference(
        ReferenceOwnership? expected,
        DateTimeOffset now,
        ClusterDirectoryEntry? actual)
    {
        if (expected is null || expected.LeaseExpiration <= now)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.ClusterId, actual.ClusterId);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.TopologyEpoch, actual.TopologyEpoch);
        Assert.Equal(expected.FencingToken, actual.FencingToken);
        Assert.Equal(expected.LeaseExpiration, actual.LeaseExpiration);
    }

    private sealed record ReferenceOwnership(
        string ClusterId,
        long Version,
        long TopologyEpoch,
        long FencingToken,
        DateTimeOffset LeaseExpiration);
}
