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
        var cancellationToken = TestContext.Current.CancellationToken;
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
            }, cancellationToken);

            var completion = Task.Run(() =>
            {
                start.SignalAndWait();
                lock (activation)
                {
                    Assert.True(tracker.RemoveRunning(message));
                    ReturnRequestTrackerIfEmpty(activation);
                }
            }, cancellationToken);

            start.SignalAndWait(cancellationToken);
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
    public void ReturningNonEmptyTracker_ThrowsAndLeaseCanBeCompletedNormally()
    {
        var tracker = new ActivationRequestTracker();
        var message = CreateMessage(1);
        tracker.OnRent();
        tracker.AddWaiting(message);

        var exception = Assert.Throws<InvalidOperationException>(tracker.OnReturn);

        Assert.Equal("An activation request tracker can only be returned when it is empty.", exception.Message);
        Assert.Same(message, Assert.Single(tracker.WaitingRequests!).Message);
        tracker.RemoveWaitingAt(0);
        tracker.OnReturn();
    }

    [Fact]
    public async Task ConcurrentFirstRequests_UseOneTrackerAndPreserveEveryRequest()
    {
        const int ProducerCount = 64;
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = CreateActivation();
        var messages = Enumerable.Range(0, ProducerCount).Select(id => CreateMessage(id)).ToArray();
        var observedTrackers = new ActivationRequestTracker[ProducerCount];
        using var start = new Barrier(ProducerCount);

        var producers = Enumerable.Range(0, ProducerCount).Select(index =>
            Task.Factory.StartNew(() =>
            {
                SignalAndWait(start, cancellationToken);
                lock (activation)
                {
                    var tracker = GetRequestTracker(activation) ??= ActivationRequestTracker.Rent();
                    observedTrackers[index] = tracker;
                    tracker.AddWaiting(messages[index]);
                }
            }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(producers);

        var tracker = Assert.IsType<ActivationRequestTracker>(GetRequestTracker(activation));
        Assert.All(observedTrackers, observed => Assert.Same(tracker, observed));
        Assert.Equal(ProducerCount, tracker.WaitingCount);
        Assert.True(messages.ToHashSet().SetEquals(tracker.WaitingRequests!.Select(entry => entry.Message)));

        lock (activation)
        {
            foreach (var message in messages)
            {
                Assert.True(tracker.TryRemoveWaitingRequest(message.SendingGrain, message.Id, out var removed));
                Assert.Same(message, removed);
            }

            ReturnRequestTrackerIfEmpty(activation);
        }

        Assert.Null(GetRequestTracker(activation));
    }

    [Fact]
    public void PooledTrackers_PreserveIsolationAcrossDeterministicModelTrace()
    {
        const int ActivationCount = 16;
        const int OperationCount = 50_000;
        var random = new Random(42);
        var activations = Enumerable.Range(0, ActivationCount).Select(_ => CreateActivation()).ToArray();
        var models = Enumerable.Range(0, ActivationCount).Select(_ => new RequestModel()).ToArray();
        var nextMessageId = 0;

        for (var operation = 0; operation < OperationCount; operation++)
        {
            var activationIndex = random.Next(ActivationCount);
            var activation = activations[activationIndex];
            var model = models[activationIndex];
            lock (activation)
            {
                var tracker = GetRequestTracker(activation);
                switch (random.Next(5))
                {
                    case 0:
                    default:
                        {
                            tracker ??= GetRequestTracker(activation) = ActivationRequestTracker.Rent();
                            var message = CreateMessage(nextMessageId++, isLocalOnly: nextMessageId % 7 == 0);
                            tracker.AddWaiting(message);
                            model.Waiting.Add(message);
                            break;
                        }
                    case 1 when model.Waiting.Count > 0:
                        {
                            var index = random.Next(model.Waiting.Count);
                            var message = model.Waiting[index];
                            tracker!.RemoveWaitingAt(index);
                            tracker.AddRunning(message, CoarseStopwatch.StartNew());
                            model.Waiting.RemoveAt(index);
                            model.Running.Add(message);
                            break;
                        }
                    case 2 when model.Running.Count > 0:
                        {
                            var message = model.Running.ElementAt(random.Next(model.Running.Count));
                            Assert.True(tracker!.RemoveRunning(message));
                            model.Running.Remove(message);
                            ReturnRequestTrackerIfEmpty(activation);
                            tracker = GetRequestTracker(activation);
                            break;
                        }
                    case 3 when model.Waiting.Count > 0:
                        {
                            var index = random.Next(model.Waiting.Count);
                            var message = model.Waiting[index];
                            Assert.True(tracker!.TryRemoveWaitingRequest(message.SendingGrain, message.Id, out var removed));
                            Assert.Same(message, removed);
                            model.Waiting.RemoveAt(index);
                            ReturnRequestTrackerIfEmpty(activation);
                            tracker = GetRequestTracker(activation);
                            break;
                        }
                    case 4 when model.Waiting.Count > 0:
                        {
                            var expected = model.Waiting.Where(message => !message.IsLocalOnly).ToArray();
                            Assert.Equal(expected, tracker!.DequeueAllWaitingRequests());
                            model.Waiting.Clear();
                            ReturnRequestTrackerIfEmpty(activation);
                            tracker = GetRequestTracker(activation);
                            break;
                        }
                }

                AssertTrackerMatchesModel(tracker, model);
            }
        }

        for (var i = 0; i < ActivationCount; i++)
        {
            var activation = activations[i];
            var model = models[i];
            lock (activation)
            {
                var tracker = GetRequestTracker(activation);
                if (tracker is null)
                {
                    Assert.Empty(model.Waiting);
                    Assert.Empty(model.Running);
                    continue;
                }

                tracker.DequeueAllWaitingRequests();
                model.Waiting.Clear();
                foreach (var message in model.Running)
                {
                    Assert.True(tracker.RemoveRunning(message));
                }

                model.Running.Clear();
                ReturnRequestTrackerIfEmpty(activation);
                AssertTrackerMatchesModel(GetRequestTracker(activation), model);
            }
        }
    }

    [Fact]
    public void ReturnedTracker_DoesNotRetainActivationMessageOrCallback()
    {
        var references = CreateReturnedTrackerWeakReferences();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        Assert.False(references.Activation.IsAlive);
        Assert.False(references.Message.IsAlive);
        Assert.False(references.Callback.IsAlive);
    }

    [Fact]
    public async Task ConcurrentReaders_ObserveConsistentActivationSnapshots()
    {
        const int Iterations = 1_000;
        var activation = CreateActivation();
        var tracker = LeaseTracker(activation);
        var message = CreateMessage(1);
        using var phase = new Barrier(5);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

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
        }, cancellation.Token)).ToArray();

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
        }, cancellation.Token);

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

    private static void AssertTrackerMatchesModel(ActivationRequestTracker? tracker, RequestModel model)
    {
        if (model.Waiting.Count == 0 && model.Running.Count == 0)
        {
            Assert.Null(tracker);
            return;
        }

        Assert.NotNull(tracker);
        Assert.Equal(model.Waiting, tracker.WaitingRequests?.Select(entry => entry.Message).ToArray() ?? []);
        Assert.Equal(model.Running.Count, tracker.RunningCount);
        Assert.True(model.Running.SetEquals((IEnumerable<Message>?)tracker.RunningRequests?.Keys ?? []));
        Assert.Equal(model.Waiting.Count + model.Running.Count, tracker.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Activation, WeakReference Message, WeakReference Callback) CreateReturnedTrackerWeakReferences()
    {
        var activation = CreateActivation();
        var callback = new object();
        var message = CreateMessage(1);
        message.BodyObject = callback;
        lock (activation)
        {
            var tracker = GetRequestTracker(activation) = ActivationRequestTracker.Rent();
            tracker.AddWaiting(message);
            tracker.RemoveWaitingAt(0);
            ReturnRequestTrackerIfEmpty(activation);
        }

        return (new(activation), new(message), new(callback));
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

    private sealed class RequestModel
    {
        public List<Message> Waiting { get; } = [];
        public HashSet<Message> Running { get; } = [];
    }
}
