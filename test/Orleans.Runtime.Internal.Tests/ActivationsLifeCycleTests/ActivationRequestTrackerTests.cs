using System.Runtime.CompilerServices;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class ActivationRequestTrackerTests
{
    [Fact]
    public void EmptyToActiveToEmptyTransitions_ReturnTrackerExactlyWhenLastRequestCompletes()
    {
        var activation = CreateActivation();
        var tracker = LeaseTracker(activation);
        var message = CreateMessage(1);

        tracker.AddWaiting(message);
        Assert.Equal(1, activation.WaitingCount);
        Assert.Equal(1, activation.GetRequestCount());
        Assert.False(activation.IsCurrentlyExecuting);
        Assert.False(activation.IsInactive);

        lock (activation)
        {
            tracker.RemoveWaitingAt(0);
            tracker.AddRunning(message, CoarseStopwatch.StartNew());
        }

        Assert.Equal(0, activation.WaitingCount);
        Assert.Equal(1, activation.GetRequestCount());
        Assert.True(activation.IsCurrentlyExecuting);
        Assert.False(activation.IsInactive);

        lock (activation)
        {
            Assert.True(tracker.RemoveRunning(message));
            ReturnRequestTrackerIfEmpty(activation);
        }

        Assert.Null(GetRequestTracker(activation));
        Assert.Equal(0, activation.GetRequestCount());
        Assert.False(activation.IsCurrentlyExecuting);
        Assert.True(activation.IsInactive);
    }

    [Fact]
    public async Task CancellationAndCompletionRace_PreservesSingleTrackerOwnership()
    {
        const int Iterations = 1_000;
        for (var i = 0; i < Iterations; i++)
        {
            var activation = CreateActivation();
            var tracker = LeaseTracker(activation);
            var message = CreateMessage(i);
            tracker.AddRunning(message, CoarseStopwatch.StartNew());
            using var start = new Barrier(3);

            Message? cancellationTarget = null;
            var cancellation = Task.Run(() =>
            {
                start.SignalAndWait();
                lock (activation)
                {
                    GetRequestTracker(activation)?.TryFindRunningRequest(message.SendingGrain, message.Id, out cancellationTarget);
                }
            });

            var completion = Task.Run(() =>
            {
                start.SignalAndWait();
                lock (activation)
                {
                    Assert.True(tracker.RemoveRunning(message));
                    ReturnRequestTrackerIfEmpty(activation);
                }
            });

            start.SignalAndWait();
            await Task.WhenAll(cancellation, completion);

            Assert.True(cancellationTarget is null || ReferenceEquals(message, cancellationTarget));
            Assert.Null(GetRequestTracker(activation));
            Assert.True(activation.IsInactive);
        }
    }

    [Fact]
    public void OverloadCount_RemainsExactAcrossWaitingAndRunningTransitions()
    {
        var activation = CreateActivation();
        var tracker = LeaseTracker(activation);
        var first = CreateMessage(1);
        var second = CreateMessage(2);

        tracker.AddWaiting(first);
        tracker.AddWaiting(second);
        Assert.Equal(2, activation.GetRequestCount());

        lock (activation)
        {
            tracker.RemoveWaitingAt(0);
            tracker.AddRunning(first, CoarseStopwatch.StartNew());
        }

        Assert.Equal(2, activation.GetRequestCount());
        Assert.Equal(1, activation.WaitingCount);
        Assert.True(activation.IsCurrentlyExecuting);

        lock (activation)
        {
            Assert.True(tracker.RemoveRunning(first));
        }

        Assert.Equal(1, activation.GetRequestCount());
        Assert.False(activation.IsCurrentlyExecuting);

        lock (activation)
        {
            tracker.RemoveWaitingAt(0);
            ReturnRequestTrackerIfEmpty(activation);
        }

        Assert.Equal(0, activation.GetRequestCount());
        Assert.True(activation.IsInactive);
    }

    [Fact]
    public void DeactivationDrain_PreservesRunningOwnershipAndFiltersLocalRequests()
    {
        var activation = CreateActivation();
        var tracker = LeaseTracker(activation);
        var running = CreateMessage(1);
        var queued = CreateMessage(2);
        var local = CreateMessage(3, isLocalOnly: true);
        tracker.AddRunning(running, CoarseStopwatch.StartNew());
        tracker.AddWaiting(queued);
        tracker.AddWaiting(local);

        var drained = activation.DequeueAllWaitingRequests();

        Assert.Same(queued, Assert.Single(drained));
        Assert.Same(tracker, GetRequestTracker(activation));
        Assert.Equal(0, activation.WaitingCount);
        Assert.Equal(1, activation.GetRequestCount());
        Assert.True(activation.IsCurrentlyExecuting);

        lock (activation)
        {
            Assert.True(tracker.RemoveRunning(running));
            ReturnRequestTrackerIfEmpty(activation);
        }

        Assert.Null(GetRequestTracker(activation));
        Assert.True(activation.IsInactive);
    }

    [Fact]
    public void ReusedTracker_DoesNotExposeStaleCollectionsOrMessages()
    {
        var tracker = new ActivationRequestTracker();
        var stale = CreateMessage(1);
        tracker.OnRent();
        tracker.AddWaiting(stale);
        tracker.RemoveWaitingAt(0);
        tracker.OnReturn();

        tracker.OnRent();
        var current = CreateMessage(2);
        tracker.AddWaiting(current);

        Assert.Equal(1, tracker.Count);
        Assert.Same(current, Assert.Single(tracker.WaitingRequests!).Message);
        Assert.DoesNotContain(tracker.WaitingRequests!, entry => ReferenceEquals(entry.Message, stale));

        tracker.RemoveWaitingAt(0);
        tracker.OnReturn();
    }

    [Fact]
    public void ReturningTrackerTwice_ThrowsBeforePoolCanOwnDuplicateLeases()
    {
        var tracker = new ActivationRequestTracker();
        tracker.OnRent();
        tracker.OnReturn();

        var exception = Assert.Throws<InvalidOperationException>(tracker.OnReturn);

        Assert.Equal("An activation request tracker cannot be returned more than once.", exception.Message);
    }

    [Fact]
    public async Task ConcurrentReaders_ObserveConsistentActivationSnapshots()
    {
        const int Iterations = 1_000;
        var activation = CreateActivation();
        var tracker = LeaseTracker(activation);
        var message = CreateMessage(1);
        using var phase = new Barrier(5);
        using var cancellation = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < Iterations; i++)
                {
                    SignalAndWait(phase, cancellation.Token);
                    Assert.Equal((1, false), activation.GetRequestStatus());
                    Assert.Equal(1, activation.GetRequestCount());
                    Assert.False(activation.IsCurrentlyExecuting);
                    SignalAndWait(phase, cancellation.Token);

                    SignalAndWait(phase, cancellation.Token);
                    Assert.Equal((0, false), activation.GetRequestStatus());
                    Assert.Equal(1, activation.GetRequestCount());
                    Assert.True(activation.IsCurrentlyExecuting);
                    SignalAndWait(phase, cancellation.Token);

                    SignalAndWait(phase, cancellation.Token);
                    Assert.Equal((0, true), activation.GetRequestStatus());
                    Assert.Equal(0, activation.GetRequestCount());
                    Assert.False(activation.IsCurrentlyExecuting);
                    SignalAndWait(phase, cancellation.Token);
                }
            }
            catch
            {
                cancellation.Cancel();
                throw;
            }
        })).ToArray();

        var writer = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < Iterations; i++)
                {
                    lock (activation)
                    {
                        tracker.AddWaiting(message);
                    }

                    SignalAndWait(phase, cancellation.Token);
                    SignalAndWait(phase, cancellation.Token);

                    lock (activation)
                    {
                        tracker.RemoveWaitingAt(0);
                        tracker.AddRunning(message, CoarseStopwatch.StartNew());
                    }

                    SignalAndWait(phase, cancellation.Token);
                    SignalAndWait(phase, cancellation.Token);

                    lock (activation)
                    {
                        Assert.True(tracker.RemoveRunning(message));
                    }

                    SignalAndWait(phase, cancellation.Token);
                    SignalAndWait(phase, cancellation.Token);
                }
            }
            catch
            {
                cancellation.Cancel();
                throw;
            }
        });

        await Task.WhenAll([.. readers, writer]);

        lock (activation)
        {
            ReturnRequestTrackerIfEmpty(activation);
        }

        Assert.Null(GetRequestTracker(activation));
    }

    private static ActivationData CreateActivation()
        => (ActivationData)RuntimeHelpers.GetUninitializedObject(typeof(ActivationData));

    private static ActivationRequestTracker LeaseTracker(ActivationData activation)
    {
        var tracker = new ActivationRequestTracker();
        tracker.OnRent();
        GetRequestTracker(activation) = tracker;
        return tracker;
    }

    private static Message CreateMessage(long id, bool isLocalOnly = false)
        => new()
        {
            Id = new CorrelationId(id),
            SendingGrain = GrainId.Create("request-tracker-test", id.ToString()),
            IsLocalOnly = isLocalOnly,
            IsKeepAlive = false,
        };

    private static void SignalAndWait(Barrier barrier, CancellationToken cancellationToken)
    {
        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10), cancellationToken))
        {
            throw new TimeoutException("Timed out waiting for activation request tracking phase participants.");
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_requestTracker")]
    private static extern ref ActivationRequestTracker? GetRequestTracker(ActivationData activation);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ReturnRequestTrackerIfEmpty")]
    private static extern void ReturnRequestTrackerIfEmpty(ActivationData activation);
}
