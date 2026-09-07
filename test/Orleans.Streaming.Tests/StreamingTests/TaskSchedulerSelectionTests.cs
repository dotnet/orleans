using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public class TaskSchedulerSelectionTests
{
    [Fact]
    public void BatchWorker_FollowUpCycleUsesCallingSchedulerAndPreservesOrder()
    {
        var scheduler = new ManualTaskScheduler();
        var observedCycles = new ConcurrentQueue<(int Cycle, TaskScheduler Scheduler)>();
        var activeCycles = 0;
        var nextCycle = 0;
        var worker = new BatchWorkerFromDelegate(() =>
        {
            Assert.Equal(0, Interlocked.Exchange(ref activeCycles, 1));
            try
            {
                observedCycles.Enqueue((Interlocked.Increment(ref nextCycle), TaskScheduler.Current));
                return Task.CompletedTask;
            }
            finally
            {
                Volatile.Write(ref activeCycles, 0);
            }
        });

        Task? serviced = null;
        var notification = Task.Factory.StartNew(
            () =>
            {
                worker.Notify();
                worker.Notify();
                serviced = worker.WaitForCurrentWorkToBeServiced();
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler);

        scheduler.ExecuteAll();

        Assert.True(notification.IsCompletedSuccessfully);
        Assert.NotNull(serviced);
        Assert.True(serviced.IsCompletedSuccessfully);
        var cycles = observedCycles.ToArray();
        Assert.Equal([1, 2], cycles.Select(entry => entry.Cycle));
        Assert.All(cycles, entry => Assert.Same(scheduler, entry.Scheduler));
        Assert.True(worker.IsIdle());
    }

    [Fact]
    public async Task BatchWorker_FailureRemainsObservableOnCallingScheduler()
    {
        var scheduler = new ManualTaskScheduler();
        var expectedException = new InvalidOperationException("Expected failure");
        Task? work = null;
        TaskScheduler? observedScheduler = null;
        var worker = new BatchWorkerFromDelegate(() =>
        {
            observedScheduler = TaskScheduler.Current;
            return Task.FromException(expectedException);
        });

        var notification = Task.Factory.StartNew(
            () => work = worker.NotifyAndWaitForWorkToBeServiced(),
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler);

        scheduler.ExecuteAll();

        Assert.True(notification.IsCompletedSuccessfully);
        Assert.Same(scheduler, observedScheduler);
        Assert.NotNull(work);
        Assert.True(work.IsFaulted);
        Assert.Same(expectedException, await Assert.ThrowsAsync<InvalidOperationException>(() => work));
        Assert.True(worker.IsIdle());
    }

    [Fact]
    public async Task BatchWorker_CancellationRemainsObservableOnCallingScheduler()
    {
        var scheduler = new ManualTaskScheduler();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task? work = null;
        TaskScheduler? observedScheduler = null;
        var worker = new BatchWorkerFromDelegate(() =>
        {
            observedScheduler = TaskScheduler.Current;
            return Task.FromCanceled(cancellation.Token);
        });

        var notification = Task.Factory.StartNew(
            () => work = worker.NotifyAndWaitForWorkToBeServiced(),
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler);

        scheduler.ExecuteAll();

        Assert.True(notification.IsCompletedSuccessfully);
        Assert.Same(scheduler, observedScheduler);
        Assert.NotNull(work);
        Assert.True(work.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.True(worker.IsIdle());
    }

    [Fact]
    public async Task StreamSubscriptionManager_QueryProjectionUsesCallingScheduler()
    {
        var scheduler = new ManualTaskScheduler();
        var pubSub = Substitute.For<IStreamPubSub>();
        var subscriptions = new List<StreamSubscription>();
        var query = new TaskCompletionSource<List<StreamSubscription>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<QualifiedStreamId, GrainId, Task<List<StreamSubscription>>> getAllSubscriptions = pubSub.GetAllSubscriptions;
        getAllSubscriptions(Arg.Any<QualifiedStreamId>(), Arg.Any<GrainId>()).Returns(query.Task);
        var manager = new StreamSubscriptionManager(pubSub, StreamSubscriptionManagerType.ExplicitSubscribeOnly);

        Task<IEnumerable<StreamSubscription>>? result = null;
        var invocation = Task.Factory.StartNew(
            () => result = manager.GetSubscriptions("provider", StreamId.Create("namespace", "key")),
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler);

        scheduler.ExecuteAll();
        query.SetResult(subscriptions);

        Assert.True(invocation.IsCompletedSuccessfully);
        Assert.NotNull(result);
        Assert.False(result.IsCompleted);
        Assert.Equal(1, scheduler.PendingTaskCount);

        scheduler.ExecuteAll();

        Assert.True(result.IsCompletedSuccessfully);
        Assert.Same(subscriptions, await result);
    }

    private sealed class ManualTaskScheduler : TaskScheduler
    {
        private readonly Queue<Task> pendingTasks = new();

        public int PendingTaskCount
        {
            get
            {
                lock (pendingTasks)
                {
                    return pendingTasks.Count;
                }
            }
        }

        public void ExecuteAll()
        {
            while (TryDequeue(out var task))
            {
                Assert.True(TryExecuteTask(task));
            }
        }

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            lock (pendingTasks)
            {
                return pendingTasks.ToArray();
            }
        }

        protected override void QueueTask(Task task)
        {
            lock (pendingTasks)
            {
                pendingTasks.Enqueue(task);
            }
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        private bool TryDequeue(out Task task)
        {
            lock (pendingTasks)
            {
                return pendingTasks.TryDequeue(out task!);
            }
        }
    }
}
