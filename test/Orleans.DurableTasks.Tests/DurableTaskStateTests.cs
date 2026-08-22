#nullable enable
using System;
using System.Distributed.DurableTasks;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization;
using Xunit;

namespace Orleans.DurableTasks.Tests;

[TestCategory("BVT")]
public class DurableTaskStateTests
{
    [Fact]
    public void NewInstance_HasEmptyCollectionsAndNullTimestamps()
    {
        var state = new DurableTaskState();

        Assert.Empty(state.CompletionDestinations);
        Assert.Empty(state.LegacyObservers);
        Assert.Null(state.Result);
        Assert.Null(state.Request);
        Assert.Null(state.CompletedAt);
        Assert.Null(state.CancellationRequestedAt);
        Assert.Equal(default, state.CreatedAt);
    }

    [Fact]
    public void MigrateLegacyObservers_EmptyLegacyObservers_ReturnsFalseAndLeavesCompletionDestinationsUnchanged()
    {
        var existingDestination = GrainId.Create("grain-type", "existing");
        var state = new DurableTaskState();
        state.CompletionDestinations.Add(existingDestination);

        var changed = state.MigrateLegacyObservers();

        Assert.False(changed);
        Assert.Equal(new[] { existingDestination }, state.CompletionDestinations.ToArray());
    }

    [Fact]
    public void MigrateLegacyObservers_OnlyNonGrainReferenceObservers_ClearsLegacyObserversAndReturnsTrue()
    {
        var state = new DurableTaskState();
        var nonGrainObserver = Substitute.For<IDurableTaskObserver>();
        state.LegacyObservers.Add(nonGrainObserver);

        var changed = state.MigrateLegacyObservers();

        Assert.True(changed);
        Assert.Empty(state.LegacyObservers);
        Assert.Empty(state.CompletionDestinations);
    }

    [Fact]
    public void MigrateLegacyObservers_GrainReferenceAlreadyInCompletionDestinations_StillReturnsTrueBecauseListIsCleared()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var observer = FakeGrainReferenceObserver.Create(services, "observer-grain-type", "observer-key");

        var state = new DurableTaskState();
        state.CompletionDestinations.Add(observer.GrainId);
        state.LegacyObservers.Add(observer);

        var changed = state.MigrateLegacyObservers();

        // The GrainId was already present, so HashSet.Add returns false internally, but the method still
        // reports a change because LegacyObservers itself was non-empty and got cleared.
        Assert.True(changed);
        Assert.Empty(state.LegacyObservers);
        Assert.Single(state.CompletionDestinations);
        Assert.Contains(observer.GrainId, state.CompletionDestinations);
    }

    [Fact]
    public void MigrateLegacyObservers_NewGrainReference_AddsGrainIdToCompletionDestinationsAndReturnsTrue()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var observer = FakeGrainReferenceObserver.Create(services, "observer-grain-type", "new-observer-key");

        var state = new DurableTaskState();
        state.LegacyObservers.Add(observer);

        Assert.DoesNotContain(observer.GrainId, state.CompletionDestinations);

        var changed = state.MigrateLegacyObservers();

        Assert.True(changed);
        Assert.Empty(state.LegacyObservers);
        Assert.Single(state.CompletionDestinations);
        Assert.Contains(observer.GrainId, state.CompletionDestinations);
    }

    [Fact]
    public void MigrateLegacyObservers_MixOfGrainReferenceAndNonGrainReference_OnlyGrainReferenceIsMigrated()
    {
        var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var grainObserver = FakeGrainReferenceObserver.Create(services, "observer-grain-type", "mixed-key");
        var nonGrainObserver = Substitute.For<IDurableTaskObserver>();

        var state = new DurableTaskState();
        state.LegacyObservers.Add(grainObserver);
        state.LegacyObservers.Add(nonGrainObserver);

        var changed = state.MigrateLegacyObservers();

        Assert.True(changed);
        Assert.Empty(state.LegacyObservers);
        Assert.Single(state.CompletionDestinations);
        Assert.Contains(grainObserver.GrainId, state.CompletionDestinations);
    }
}
