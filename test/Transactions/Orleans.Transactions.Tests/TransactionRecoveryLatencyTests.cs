using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Timers.Internal;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Diagnostics;
using Orleans.Transactions.State;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionRecoveryLatencyTests
{
    [Fact]
    public async Task TransactionTimeoutIsPreservedAcrossForks()
    {
        var timeout = TimeSpan.FromSeconds(30);

        var transaction = await CreateTransactionAgent(TransactionAgentProtocol.Instance).StartTransaction(readOnly: false, timeout);
        var fork = transaction.Fork();

        Assert.Equal(timeout, transaction.Timeout);
        Assert.Equal(timeout, fork.Timeout);
    }

    [Fact]
    public async Task ParticipantLockUsesTransactionTimeoutWhenItExceedsConfiguredLockTimeout()
    {
        var resource = CreateParticipant("resource", ParticipantId.Role.Resource);
        var activationLifetime = new TestActivationLifetime();
        var configuredLockTimeout = TimeSpan.FromSeconds(8);
        var transactionTimeout = TimeSpan.FromSeconds(30);
        var queue = new GatedCancelTransactionQueue(
            resource,
            activationLifetime,
            options: new TransactionalStateOptions { LockTimeout = configuredLockTimeout });
        var transactionId = Guid.NewGuid();
        var before = DateTime.UtcNow + transactionTimeout;

        await queue.RWLock.EnterLock(
            transactionId,
            DateTime.UtcNow,
            transactionTimeout,
            default,
            isRead: true,
            exclusiveLock: false,
            static () => true);

        var after = DateTime.UtcNow + transactionTimeout;
        var deadline = Assert.IsType<DateTime>(queue.RWLock.CurrentGroupDeadline);
        Assert.InRange(deadline, before, after);
        Assert.Equal(
            transactionTimeout,
            ReadWriteLock<TestState>.GetEffectiveLockTimeout(transactionTimeout, configuredLockTimeout));
        Assert.Equal(
            configuredLockTimeout,
            ReadWriteLock<TestState>.GetEffectiveLockTimeout(TimeSpan.Zero, configuredLockTimeout));

        queue.RWLock.Rollback(transactionId);
        activationLifetime.Cancel();
    }

    [Fact]
    public void RestoredRemoteCommitUsesBoundedExponentialPingRetry()
    {
        var frequency = TimeSpan.FromSeconds(60);
        var sentAt = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var record = new TransactionRecord<TestState>
        {
            Role = CommitRole.RemoteCommit,
            LastSent = DateTime.MinValue,
            IsRestoredRemoteCommit = true,
        };

        Assert.Equal(DateTime.MinValue, record.GetNextRemotePingAt(frequency));

        foreach (var expectedDelay in new[] { 1, 2, 4, 8, 16, 32, 60, 60 })
        {
            record.RecordRemotePingSent(sentAt);
            Assert.Equal(sentAt.AddSeconds(expectedDelay), record.GetNextRemotePingAt(frequency));
            sentAt = record.GetNextRemotePingAt(frequency);
        }
    }

    [Fact]
    public void FreshRemoteCommitRetainsFirstPingGraceThenUsesBoundedExponentialRetry()
    {
        var frequency = TransactionalStateOptions.DefaultRemoteTransactionPingFrequency;
        var sentAt = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var record = new TransactionRecord<TestState>
        {
            Role = CommitRole.RemoteCommit,
            LastSent = sentAt,
        };

        Assert.Equal(sentAt.AddSeconds(60), record.GetNextRemotePingAt(frequency));

        sentAt = sentAt.Add(frequency);
        foreach (var expectedDelay in new[] { 1, 2, 4, 8, 16, 32, 60, 60 })
        {
            record.RecordRemotePingSent(sentAt);
            Assert.Equal(sentAt.AddSeconds(expectedDelay), record.GetNextRemotePingAt(frequency));
            sentAt = record.GetNextRemotePingAt(frequency);
        }
    }

    [Fact]
    public async Task CancelBeforePrepareBreaksPrePrepareLockAndRetainsTransactionId()
    {
        var resource = CreateParticipant("resource", ParticipantId.Role.Resource);
        var queue = new GatedCancelTransactionQueue(resource, new TestActivationLifetime());
        var transactionId = Guid.NewGuid();
        var timeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var accessCount = new AccessCounter { Writes = 1 };

        await queue.RWLock.EnterLock(
            transactionId,
            timeStamp,
            TimeSpan.FromMinutes(1),
            default,
            isRead: false,
            exclusiveLock: false,
            static () => 0);

        await queue.NotifyOfCancel(transactionId, timeStamp, TransactionalStatus.CascadingAbort);

        var (status, record) = await queue.RWLock.ValidateLock(transactionId, accessCount);
        Assert.Equal(TransactionalStatus.BrokenLock, status);
        Assert.Equal(transactionId, record.TransactionId);
    }

    [Fact]
    public void StorageBatchTracksCommittedTransactionIds()
    {
        var firstTransactionId = Guid.NewGuid();
        var secondTransactionId = Guid.NewGuid();
        var batch = new StorageBatch<TestState>(
            new TransactionalStateMetaData(),
            etag: null,
            confirmUpTo: 0,
            cancelAbove: 0);

        batch.Commit(firstTransactionId, DateTime.UtcNow, []);
        batch.Commit(secondTransactionId, DateTime.UtcNow, []);

        Assert.Equal(2, batch.CommitCount);
        Assert.Equal(
            new[] { firstTransactionId, secondTransactionId },
            batch.CommittedTransactionIds.ToArray());
    }

    [Fact]
    public void CompletedFanOutWinsAfterCleanupDeadlineWasSelected()
    {
        var fanOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupDeadline = Task.CompletedTask;

        Assert.True(TransactionQueue<TestState>.ShouldAbandonCancelFanOut(fanOut.Task, cleanupDeadline));

        fanOut.SetResult();

        Assert.False(TransactionQueue<TestState>.ShouldAbandonCancelFanOut(fanOut.Task, cleanupDeadline));
    }

    [Fact]
    public async Task AlreadyCompletedFanOutWinsWhenCleanupDeadlineIsAlsoComplete()
    {
        var fanOut = Task.CompletedTask;
        var cleanupDeadline = Task.CompletedTask;

        var completed = await Task.WhenAny(fanOut, cleanupDeadline);

        Assert.Same(fanOut, completed);
        Assert.False(TransactionQueue<TestState>.ShouldAbandonCancelFanOut(fanOut, completed));
    }

    [Fact]
    public async Task AbortingQueuedTransactionRemovesAllPendingOperations()
    {
        var queue = new GatedCancelTransactionQueue(
            CreateParticipant("resource", ParticipantId.Role.Resource),
            new TestActivationLifetime());
        var currentTransactionId = Guid.NewGuid();
        var abortedTransactionId = Guid.NewGuid();
        var nextTransactionId = Guid.NewGuid();
        var priority = DateTime.UtcNow;
        var transactionTimeout = TimeSpan.FromMinutes(1);
        var abortedOperationCount = 0;

        await queue.RWLock.EnterLock(
            currentTransactionId,
            priority,
            transactionTimeout,
            new AccessCounter(),
            isRead: false,
            exclusiveLock: false,
            static () => 0);
        var firstAbortedOperation = queue.RWLock.EnterLock(
            abortedTransactionId,
            priority,
            transactionTimeout,
            new AccessCounter(),
            isRead: false,
            exclusiveLock: false,
            () => ++abortedOperationCount);
        var secondAbortedOperation = queue.RWLock.EnterLock(
            abortedTransactionId,
            priority,
            transactionTimeout,
            new AccessCounter { Writes = 1 },
            isRead: false,
            exclusiveLock: false,
            () => ++abortedOperationCount);

        Assert.False(firstAbortedOperation.IsCompleted);
        Assert.False(secondAbortedOperation.IsCompleted);

        queue.RWLock.Rollback(abortedTransactionId);

        await Assert.ThrowsAsync<OrleansCascadingAbortException>(
            () => firstAbortedOperation.WaitAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<OrleansCascadingAbortException>(
            () => secondAbortedOperation.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, abortedOperationCount);

        var nextOperation = queue.RWLock.EnterLock(
            nextTransactionId,
            priority,
            transactionTimeout,
            new AccessCounter(),
            isRead: false,
            exclusiveLock: false,
            static () => 42);

        Assert.False(nextOperation.IsCompleted);

        queue.RWLock.Rollback(currentTransactionId);
        queue.RWLock.Notify();

        Assert.Equal(42, await nextOperation.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, abortedOperationCount);
    }

    [Fact]
    public async Task QueuedWriteUpgradeAbortsConflictingTransactionOperations()
    {
        var queue = new GatedCancelTransactionQueue(
            CreateParticipant("resource", ParticipantId.Role.Resource),
            new TestActivationLifetime());
        var currentTransactionId = Guid.NewGuid();
        var upgradingTransactionId = Guid.NewGuid();
        var conflictingTransactionId = Guid.NewGuid();
        var priority = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var transactionTimeout = TimeSpan.FromMinutes(1);
        var conflictingOperationCount = 0;

        await queue.RWLock.EnterLock(
            currentTransactionId,
            priority,
            transactionTimeout,
            new AccessCounter(),
            isRead: false,
            exclusiveLock: false,
            static () => 0);
        var queuedRead = queue.RWLock.EnterLock(
            upgradingTransactionId,
            priority,
            transactionTimeout,
            new AccessCounter(),
            isRead: true,
            exclusiveLock: false,
            static () => 1);
        var conflictingRead = queue.RWLock.EnterLock(
            conflictingTransactionId,
            priority,
            transactionTimeout,
            new AccessCounter(),
            isRead: true,
            exclusiveLock: false,
            () => ++conflictingOperationCount);
        var queuedWrite = queue.RWLock.EnterLock(
            upgradingTransactionId,
            priority,
            transactionTimeout,
            new AccessCounter { Reads = 1 },
            isRead: false,
            exclusiveLock: false,
            static () => 2);

        await Assert.ThrowsAsync<OrleansCascadingAbortException>(
            () => conflictingRead.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, conflictingOperationCount);
        Assert.False(queuedRead.IsCompleted);
        Assert.False(queuedWrite.IsCompleted);

        queue.RWLock.Rollback(currentTransactionId);
        queue.RWLock.Notify();

        Assert.Equal(1, await queuedRead.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await queuedWrite.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, conflictingOperationCount);
    }

    [Fact]
    public async Task UnresolvableQueuedWriteUpgradeAbortsItsPendingOperations()
    {
        var queue = new GatedCancelTransactionQueue(
            CreateParticipant("resource", ParticipantId.Role.Resource),
            new TestActivationLifetime());
        var currentTransactionId = Guid.NewGuid();
        var upgradingTransactionId = Guid.NewGuid();
        var conflictingTransactionId = Guid.NewGuid();
        var higherPriority = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var lowerPriority = higherPriority.AddTicks(1);
        var transactionTimeout = TimeSpan.FromMinutes(1);
        var upgradingOperationCount = 0;
        var conflictingOperationCount = 0;

        await queue.RWLock.EnterLock(
            currentTransactionId,
            higherPriority,
            transactionTimeout,
            new AccessCounter(),
            isRead: false,
            exclusiveLock: false,
            static () => 0);
        var queuedRead = queue.RWLock.EnterLock(
            upgradingTransactionId,
            lowerPriority,
            transactionTimeout,
            new AccessCounter(),
            isRead: true,
            exclusiveLock: false,
            () => ++upgradingOperationCount);
        var conflictingRead = queue.RWLock.EnterLock(
            conflictingTransactionId,
            higherPriority,
            transactionTimeout,
            new AccessCounter(),
            isRead: true,
            exclusiveLock: false,
            () => ++conflictingOperationCount);

        await Assert.ThrowsAsync<OrleansTransactionLockUpgradeException>(
            () => queue.RWLock.EnterLock(
                upgradingTransactionId,
                lowerPriority,
                transactionTimeout,
                new AccessCounter { Reads = 1 },
                isRead: false,
                exclusiveLock: false,
                static () => 0));
        await Assert.ThrowsAsync<OrleansCascadingAbortException>(
            () => queuedRead.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, upgradingOperationCount);
        Assert.False(conflictingRead.IsCompleted);

        queue.RWLock.Rollback(currentTransactionId);
        queue.RWLock.Notify();

        Assert.Equal(1, await conflictingRead.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, upgradingOperationCount);
        Assert.Equal(1, conflictingOperationCount);
    }

    [Fact]
    public async Task LocalAbortCompletesManagerDecisionAfterDispatchAndBeforeCleanupSettles()
    {
        var manager = CreateParticipant(
            "manager",
            ParticipantId.Role.Manager | ParticipantId.Role.Resource);
        var remoteOne = CreateParticipant("remote-one", ParticipantId.Role.Resource);
        var remoteTwo = CreateParticipant("remote-two", ParticipantId.Role.Resource);
        var remoteOneDispatchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteOneGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteTwoGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationId = ActivationId.NewId();
        var identity = new TransactionDiagnosticEvents.TransactionDiagnosticIdentity(null, activationId);
        var queue = new GatedCancelTransactionQueue(
            manager,
            new TestActivationLifetime(),
            new Dictionary<string, Task>
            {
                [remoteOne.Name] = remoteOneGate.Task,
                [remoteTwo.Name] = remoteTwoGate.Task,
            },
            new Dictionary<string, Task>
            {
                [remoteOne.Name] = remoteOneDispatchGate.Task,
            },
            identity);
        var protocol = new ManagerAbortProtocol(queue);
        var agent = CreateTransactionAgent(protocol);
        var transactionId = Guid.NewGuid();
        var timeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var transaction = new TransactionInfo(transactionId, timeStamp, timeStamp);
        transaction.RecordWrite(manager, timeStamp);
        transaction.RecordWrite(remoteOne, timeStamp);
        transaction.RecordWrite(remoteTwo, timeStamp);
        var observer = new RecordingObserver(transactionId);
        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(observer);

        var resolution = Task.Run(
            async () => await agent.Resolve(transaction),
            TestContext.Current.CancellationToken);
        await queue.WaitForCancelInvocationAsync(TestContext.Current.CancellationToken);

        var promise = Assert.IsType<TaskCompletionSource<TransactionalStatus>>(protocol.ManagerPromise);
        Assert.False(promise.Task.IsCompleted);
        Assert.False(resolution.IsCompleted);
        Assert.Equal(remoteOne.Name, Assert.Single(queue.CancelInvocations).Target.Name);

        remoteOneDispatchGate.TrySetResult();
        await queue.WaitForCancelInvocationAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TransactionalStatus.PrepareTimeout, await promise.Task.WaitAsync(TestContext.Current.CancellationToken));
        var (status, exception) = await resolution.WaitAsync(TestContext.Current.CancellationToken);

        var managerFanOut = Assert.IsAssignableFrom<Task>(protocol.ManagerFanOutTask);
        Assert.False(managerFanOut.IsCompleted);
        Assert.Equal(TransactionalStatus.PrepareTimeout, status);
        Assert.Null(exception);
        Assert.Equal(0, protocol.TransactionAgentCancelCount);
        Assert.Collection(
            observer.Events.Take(4),
            evt => Assert.IsType<TransactionDiagnosticEvents.CancelFanOutStarted>(evt),
            evt => Assert.IsType<TransactionDiagnosticEvents.CancelSendStarted>(evt),
            evt => Assert.IsType<TransactionDiagnosticEvents.CancelSendStarted>(evt),
            evt =>
            {
                var decision = Assert.IsType<TransactionDiagnosticEvents.TransactionManagerAbortDecisionCompleted>(evt);
                Assert.Equal(TransactionalStatus.PrepareTimeout, decision.Status);
            });

        remoteTwoGate.TrySetResult();
        remoteOneGate.TrySetResult();
        await Task.WhenAll(queue.CancelInvocations.Select(send => send.SendTask))
            .WaitAsync(TestContext.Current.CancellationToken);
        await managerFanOut.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(managerFanOut.IsCompletedSuccessfully);
        Assert.Equal(
            new[] { remoteOne.Name, remoteTwo.Name },
            queue.CancelInvocations.Select(send => send.Target.Name).Order());
        Assert.All(
            queue.CancelInvocations,
            send =>
            {
                Assert.Equal(TransactionalStatus.PrepareTimeout, send.Status);
                Assert.Equal(TransactionDiagnosticEvents.CancelReason.TransactionAbort, send.Reason);
            });
        Assert.IsType<TransactionDiagnosticEvents.CancelFanOutCompleted>(observer.Events[^1]);
        Assert.All(observer.Events, evt => Assert.Equal(activationId, evt.ActivationId));
    }

    [Fact]
    public async Task LocalAbortWithCanceledActivationDispatchesOnceAndCompletesOriginalDecision()
    {
        var reference = CreateGrainReference("already-deactivating");
        var manager = CreateParticipant("manager", reference, ParticipantId.Role.Manager);
        var remote = CreateParticipant("remote", ParticipantId.Role.Resource);
        var selfResource = CreateParticipant("self-resource", reference, ParticipantId.Role.Resource);
        var remoteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selfGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new TestActivationLifetime();
        lifetime.Cancel();
        var queue = new GatedCancelTransactionQueue(
            manager,
            lifetime,
            new Dictionary<string, Task>
            {
                [remote.Name] = remoteGate.Task,
                [selfResource.Name] = selfGate.Task,
            });
        var protocol = new ManagerAbortProtocol(queue);
        var agent = CreateTransactionAgent(protocol);
        var timeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var transaction = new TransactionInfo(Guid.NewGuid(), timeStamp, timeStamp);
        transaction.RecordWrite(manager, timeStamp);
        transaction.RecordWrite(remote, timeStamp);
        transaction.RecordWrite(selfResource, timeStamp);

        var resolution = agent.Resolve(transaction);
        var (status, exception) = await resolution.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(TransactionalStatus.PrepareTimeout, status);
        Assert.Null(exception);
        Assert.Equal(2, queue.CancelSendCount);
        Assert.Equal(
            TransactionalStatus.PrepareTimeout,
            await protocol.ManagerPromise!.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(protocol.ManagerFanOutTask!.IsCompletedSuccessfully);
        Assert.Equal(2, queue.CancelSendCount);
        Assert.Equal(1, queue.CancelInvocations.Count(send => send.Target.Equals(remote)));
        Assert.Equal(1, queue.CancelInvocations.Count(send => send.Target.Equals(selfResource)));
        Assert.Contains(queue.CancelInvocations, send => send.Target.Equals(selfResource) && send.IsSelf);
        Assert.All(
            queue.CancelInvocations,
            send =>
            {
                Assert.False(send.SendTask.IsCompleted);
                Assert.Equal(TransactionalStatus.PrepareTimeout, send.Status);
                Assert.Equal(TransactionDiagnosticEvents.CancelReason.TransactionAbort, send.Reason);
            });
        Assert.Equal(1, protocol.ManagerFanOutCount);
        Assert.Equal(0, protocol.TransactionAgentCancelCount);

        var remoteSend = queue.CancelInvocations.Single(send => send.Target.Equals(remote));
        var selfSend = queue.CancelInvocations.Single(send => send.Target.Equals(selfResource));
        remoteGate.TrySetResult();
        await remoteSend.SendTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(selfSend.SendTask.IsCompleted);

        selfGate.TrySetResult();
        await selfSend.SendTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LocalAbortDeactivationBoundsNeverCompletingCancelAndDiagnosesCancellation()
    {
        var manager = CreateParticipant("manager", ParticipantId.Role.Manager);
        var remote = CreateParticipant("remote", ParticipantId.Role.Resource);
        var remoteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new TestActivationLifetime();
        var queue = new GatedCancelTransactionQueue(
            manager,
            lifetime,
            new Dictionary<string, Task> { [remote.Name] = remoteGate.Task });
        var record = CreateLocalCommitRecord(manager, remote);
        var observer = new RecordingObserver(record.TransactionId);
        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(observer);

        var notification = queue.NotifyOfAbort(record, TransactionalStatus.PrepareTimeout, exception: null);
        var send = Assert.Single(queue.CancelInvocations);

        Assert.False(send.SendTask.IsCompleted);
        Assert.Equal(TransactionalStatus.PrepareTimeout, await record.PromiseForTA.Task);
        Assert.False(notification.IsCompleted);

        lifetime.Cancel();
        await notification.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(send.SendTask.IsCompleted);
        Assert.Equal(TransactionalStatus.PrepareTimeout, send.Status);
        Assert.IsType<TransactionDiagnosticEvents.TransactionManagerAbortDecisionCompleted>(
            observer.Events.Single(evt => evt is TransactionDiagnosticEvents.TransactionManagerAbortDecisionCompleted));
        var failed = Assert.Single(observer.Events.OfType<TransactionDiagnosticEvents.CancelFanOutFailed>());
        Assert.Equal(TransactionalStatus.PrepareTimeout, failed.Status);
        Assert.Equal(1, failed.TargetCount);
        Assert.Equal(0, failed.SelfTargetCount);
        Assert.EndsWith("CanceledException", failed.ExceptionType);
        Assert.Single(observer.Events.OfType<TransactionDiagnosticEvents.CancelSendStarted>());
        Assert.Empty(observer.Events.OfType<TransactionDiagnosticEvents.CancelSendCompleted>());
        Assert.Empty(observer.Events.OfType<TransactionDiagnosticEvents.CancelSendFailed>());
        Assert.IsType<TransactionDiagnosticEvents.CancelFanOutFailed>(observer.Events[^1]);

        remoteGate.TrySetResult();
        await send.SendTask.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LocalAbortCleanupTimeoutCompletesBeforeOuterDeadlineWithoutDuplicateFanOut()
    {
        var manager = CreateParticipant(
            "manager",
            ParticipantId.Role.Manager | ParticipantId.Role.Resource);
        var remote = CreateParticipant("remote", ParticipantId.Role.Resource);
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupTimeout = TimeSpan.FromMilliseconds(250);
        var queue = new GatedCancelTransactionQueue(
            manager,
            new TestActivationLifetime(),
            new Dictionary<string, Task> { [remote.Name] = cancelGate.Task },
            options: new TransactionalStateOptions { LockTimeout = cleanupTimeout });
        var protocol = new ManagerAbortProtocol(queue);
        var agent = CreateTransactionAgent(protocol);
        var timeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var transaction = new TransactionInfo(Guid.NewGuid(), timeStamp, timeStamp);
        transaction.RecordWrite(manager, timeStamp);
        transaction.RecordWrite(remote, timeStamp);
        var observer = new RecordingObserver(transaction.TransactionId);
        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(observer);
        var outerDeadline = TimeSpan.FromSeconds(2);

        var resolution = agent.Resolve(transaction);
        var (status, exception) = await resolution.WaitAsync(
            outerDeadline,
            TestContext.Current.CancellationToken);

        Assert.False(protocol.ManagerFanOutTask!.IsCompleted);

        Assert.Equal(TransactionalStatus.PrepareTimeout, status);
        Assert.Null(exception);
        Assert.Equal(1, queue.CancelSendCount);
        Assert.Equal(1, protocol.ManagerFanOutCount);
        Assert.Equal(0, protocol.TransactionAgentCancelCount);
        Assert.Equal(
            TransactionalStatus.PrepareTimeout,
            await protocol.ManagerPromise!.Task.WaitAsync(TestContext.Current.CancellationToken));
        var send = Assert.Single(queue.CancelInvocations);
        Assert.False(send.SendTask.IsCompleted);
        Assert.Equal(TransactionalStatus.PrepareTimeout, send.Status);
        Assert.Equal(TransactionDiagnosticEvents.CancelReason.TransactionAbort, send.Reason);

        await protocol.ManagerFanOutTask.WaitAsync(outerDeadline, TestContext.Current.CancellationToken);

        Assert.True(protocol.ManagerFanOutTask.IsCompletedSuccessfully);
        var failed = Assert.Single(observer.Events.OfType<TransactionDiagnosticEvents.CancelFanOutFailed>());
        Assert.Equal(typeof(TimeoutException).FullName, failed.ExceptionType);
        Assert.Equal(TransactionalStatus.PrepareTimeout, failed.Status);
        Assert.Equal(1, failed.TargetCount);
        Assert.Equal(0, failed.SelfTargetCount);
        Assert.Single(observer.Events.OfType<TransactionDiagnosticEvents.CancelSendStarted>());
        Assert.Empty(observer.Events.OfType<TransactionDiagnosticEvents.CancelSendCompleted>());
        Assert.Empty(observer.Events.OfType<TransactionDiagnosticEvents.CancelSendFailed>());
        Assert.IsType<TransactionDiagnosticEvents.TransactionManagerAbortDecisionCompleted>(
            observer.Events.Single(evt => evt is TransactionDiagnosticEvents.TransactionManagerAbortDecisionCompleted));
        Assert.IsType<TransactionDiagnosticEvents.CancelFanOutFailed>(observer.Events[^1]);

        cancelGate.TrySetResult();
        await send.SendTask.WaitAsync(outerDeadline, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SelfDirectedCancelDuringDeactivationIsInitiatedOnceAndDiagnosedAsSelf()
    {
        var reference = CreateGrainReference("shared-transactional-state");
        var manager = CreateParticipant("manager", reference, ParticipantId.Role.Manager);
        var selfResource = CreateParticipant("resource-alias", reference, ParticipantId.Role.Resource);
        var selfGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new TestActivationLifetime();
        var queue = new GatedCancelTransactionQueue(
            manager,
            lifetime,
            new Dictionary<string, Task> { [selfResource.Name] = selfGate.Task });
        var record = CreateLocalCommitRecord(manager, selfResource);
        var observer = new RecordingObserver(record.TransactionId);
        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(observer);

        var notification = queue.NotifyOfAbort(record, TransactionalStatus.PrepareTimeout, exception: null);
        var send = Assert.Single(queue.CancelInvocations);

        Assert.Equal("resource-alias", send.Target.Name);
        Assert.True(send.IsSelf);
        Assert.False(send.SendTask.IsCompleted);
        Assert.Equal(TransactionalStatus.PrepareTimeout, await record.PromiseForTA.Task);
        Assert.False(notification.IsCompleted);

        lifetime.Cancel();
        await notification;

        Assert.Equal(1, queue.CancelSendCount);
        Assert.False(send.SendTask.IsCompleted);
        var sendStarted = Assert.Single(observer.Events.OfType<TransactionDiagnosticEvents.CancelSendStarted>());
        Assert.True(sendStarted.IsSelf);
        Assert.Equal(selfResource, sendStarted.Target);
        Assert.Equal(TransactionalStatus.PrepareTimeout, sendStarted.Status);
        Assert.Equal(TransactionDiagnosticEvents.CancelReason.TransactionAbort, sendStarted.Reason);
        var failed = Assert.Single(observer.Events.OfType<TransactionDiagnosticEvents.CancelFanOutFailed>());
        Assert.Equal(TransactionalStatus.PrepareTimeout, failed.Status);
        Assert.Equal(1, failed.TargetCount);
        Assert.Equal(1, failed.SelfTargetCount);
        Assert.Empty(observer.Events.OfType<TransactionDiagnosticEvents.CancelSendCompleted>());
        Assert.Empty(observer.Events.OfType<TransactionDiagnosticEvents.CancelSendFailed>());
        Assert.IsType<TransactionDiagnosticEvents.TransactionManagerAbortDecisionCompleted>(
            observer.Events.Single(evt => evt is TransactionDiagnosticEvents.TransactionManagerAbortDecisionCompleted));
        Assert.IsType<TransactionDiagnosticEvents.CancelFanOutFailed>(observer.Events[^1]);

        selfGate.TrySetResult();
        await send.SendTask.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ManagerOwnedAbortProducesOneFanOutAndNoTransactionAgentDuplicateCancel()
    {
        var manager = CreateParticipant(
            "manager",
            ParticipantId.Role.Manager | ParticipantId.Role.Resource);
        var remote = CreateParticipant("remote", ParticipantId.Role.Resource);
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new GatedCancelTransactionQueue(
            manager,
            new TestActivationLifetime(),
            new Dictionary<string, Task> { [remote.Name] = cancelGate.Task });
        var protocol = new ManagerAbortProtocol(queue);
        var agent = CreateTransactionAgent(protocol);
        var timeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var transaction = new TransactionInfo(Guid.NewGuid(), timeStamp, timeStamp);
        transaction.RecordWrite(manager, timeStamp);
        transaction.RecordWrite(remote, timeStamp);

        var resolution = agent.Resolve(transaction);
        var (status, exception) = await resolution.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        var promise = Assert.IsType<TaskCompletionSource<TransactionalStatus>>(protocol.ManagerPromise);
        var managerFanOut = Assert.IsAssignableFrom<Task>(protocol.ManagerFanOutTask);
        Assert.False(managerFanOut.IsCompleted);
        Assert.Equal(1, protocol.ManagerFanOutCount);
        Assert.Equal(1, queue.CancelSendCount);
        Assert.Equal(0, protocol.TransactionAgentCancelCount);

        Assert.Equal(TransactionalStatus.PrepareTimeout, status);
        Assert.Null(exception);
        Assert.Equal(
            TransactionalStatus.PrepareTimeout,
            await promise.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, protocol.ManagerFanOutCount);
        Assert.Equal(1, queue.CancelSendCount);
        Assert.Equal(0, protocol.TransactionAgentCancelCount);

        cancelGate.TrySetResult();
        await managerFanOut.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(managerFanOut.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RepeatedRecoveryPingsRemainIdempotent()
    {
        var manager = CreateParticipant("manager", ParticipantId.Role.Manager);
        var remote = CreateParticipant("remote", ParticipantId.Role.Resource);
        var queue = new GatedCancelTransactionQueue(manager, new TestActivationLifetime());
        var transactionId = Guid.NewGuid();
        var timeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var observer = new RecordingObserver(transactionId);
        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(observer);

        await queue.NotifyOfPing(transactionId, timeStamp, remote)
            .WaitAsync(TestContext.Current.CancellationToken);
        await queue.NotifyOfPing(transactionId, timeStamp, remote)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, observer.Events.OfType<TransactionDiagnosticEvents.CancelSendStarted>().Count());
        Assert.Equal(2, observer.Events.OfType<TransactionDiagnosticEvents.CancelSendCompleted>().Count());
        Assert.All(
            observer.Events.OfType<TransactionDiagnosticEvents.CancelSendEvent>(),
            evt =>
            {
                Assert.Equal(TransactionalStatus.PresumedAbort, evt.Status);
                Assert.Equal(TransactionDiagnosticEvents.CancelReason.RecoveryPing, evt.Reason);
            });
    }

    private static ParticipantId CreateParticipant(string name, ParticipantId.Role role)
        => CreateParticipant(name, reference: null!, role);

    private static ParticipantId CreateParticipant(
        string name,
        GrainReference reference,
        ParticipantId.Role role) => new(name, reference, role);

    private static GrainReference CreateGrainReference(string key)
        => new TestGrainReference(GrainId.Create("transaction-recovery-test", key));

    private static TransactionAgent CreateTransactionAgent(ITransactionAgentProtocol protocol)
        => new(
            new TestClock(),
            NullLogger<TransactionAgent>.Instance,
            new TransactionAgentStatistics(),
            new NeverOverloaded(),
            protocol);

    private static TransactionRecord<TestState> CreateLocalCommitRecord(
        ParticipantId manager,
        params ParticipantId[] participants)
        => new()
        {
            Role = CommitRole.LocalCommit,
            TransactionId = Guid.NewGuid(),
            Timestamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc),
            PromiseForTA = new(TaskCreationOptions.RunContinuationsAsynchronously),
            WriteParticipants = [manager, .. participants],
        };

    private sealed class TestState
    {
    }

    private sealed class GatedCancelTransactionQueue : TransactionQueue<TestState>
    {
        private readonly IReadOnlyDictionary<string, Task> cancelGates;
        private readonly IReadOnlyDictionary<string, Task> dispatchGates;
        private readonly ConcurrentQueue<CancelInvocation> cancelInvocations = new();
        private readonly SemaphoreSlim cancelInvocationSignal = new(0);

        public int CancelSendCount => cancelInvocations.Count;
        public IReadOnlyList<CancelInvocation> CancelInvocations => cancelInvocations.ToArray();
        public Task WaitForCancelInvocationAsync(CancellationToken cancellationToken)
            => cancelInvocationSignal.WaitAsync(cancellationToken);

        public GatedCancelTransactionQueue(
            ParticipantId resource,
            IActivationLifetime activationLifetime,
            IReadOnlyDictionary<string, Task>? cancelGates = null,
            IReadOnlyDictionary<string, Task>? dispatchGates = null,
            TransactionDiagnosticEvents.TransactionDiagnosticIdentity identity = default,
            TransactionalStateOptions? options = null)
            : base(
                Options.Create(options ?? new TransactionalStateOptions()),
                resource,
                static () => { },
                null!,
                new TestClock(),
                NullLogger.Instance,
                null!,
                activationLifetime,
                identity)
        {
            this.cancelGates = cancelGates ?? new Dictionary<string, Task>();
            this.dispatchGates = dispatchGates ?? new Dictionary<string, Task>();
        }

        protected override Task SendCancel(
            ParticipantId target,
            Guid transactionId,
            DateTime timeStamp,
            TransactionalStatus status,
            TransactionDiagnosticEvents.CancelReason reason)
        {
            var isSelf = target.Reference is not null
                && Resource.Reference is not null
                && target.Reference.GrainId == Resource.Reference.GrainId;
            var gate = cancelGates.TryGetValue(target.Name, out var configuredGate)
                ? configuredGate
                : Task.CompletedTask;
            var invocation = new CancelInvocation(target, status, reason, isSelf);
            cancelInvocations.Enqueue(invocation);
            cancelInvocationSignal.Release();
            if (dispatchGates.TryGetValue(target.Name, out var dispatchGate))
            {
                dispatchGate.GetAwaiter().GetResult();
            }

            invocation.SendTask = SendCancelCore(
                invocation,
                transactionId,
                timeStamp,
                gate);
            return invocation.SendTask;
        }

        private async Task SendCancelCore(
            CancelInvocation invocation,
            Guid transactionId,
            DateTime timeStamp,
            Task gate)
        {
            TransactionDiagnosticEvents.EmitCancelSendStarted(
                Resource,
                transactionId,
                timeStamp,
                invocation.Target,
                invocation.IsSelf,
                invocation.Status,
                invocation.Reason,
                DiagnosticIdentity);
            await gate;
            TransactionDiagnosticEvents.EmitCancelSendCompleted(
                Resource,
                transactionId,
                timeStamp,
                invocation.Target,
                invocation.IsSelf,
                invocation.Status,
                invocation.Reason,
                DiagnosticIdentity);
        }
    }

    private sealed class CancelInvocation(
        ParticipantId target,
        TransactionalStatus status,
        TransactionDiagnosticEvents.CancelReason reason,
        bool isSelf)
    {
        public ParticipantId Target { get; } = target;
        public TransactionalStatus Status { get; } = status;
        public TransactionDiagnosticEvents.CancelReason Reason { get; } = reason;
        public bool IsSelf { get; } = isSelf;
        public Task SendTask { get; set; } = null!;
    }

    private sealed class ManagerAbortProtocol(GatedCancelTransactionQueue queue) : ITransactionAgentProtocol
    {
        public Task? ManagerFanOutTask { get; private set; }
        public TaskCompletionSource<TransactionalStatus>? ManagerPromise { get; private set; }
        public int ManagerFanOutCount { get; private set; }
        public int TransactionAgentCancelCount { get; private set; }

        public void Prepare(
            ParticipantId participant,
            Guid transactionId,
            AccessCounter accessCount,
            DateTime timeStamp,
            ParticipantId transactionManager)
        {
        }

        public Task<TransactionalStatus> PrepareAndCommit(
            ParticipantId transactionManager,
            Guid transactionId,
            AccessCounter accessCount,
            DateTime timeStamp,
            List<ParticipantId> writeResources,
            int totalParticipants)
        {
            var promise = new TaskCompletionSource<TransactionalStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            ManagerPromise = promise;
            var record = new TransactionRecord<TestState>
            {
                Role = CommitRole.LocalCommit,
                TransactionId = transactionId,
                Timestamp = timeStamp,
                PromiseForTA = promise,
                WriteParticipants = writeResources,
            };

            ManagerFanOutCount++;
            ManagerFanOutTask = queue.NotifyOfAbort(record, TransactionalStatus.PrepareTimeout, exception: null);
            return promise.Task;
        }

        public Task Cancel(
            ParticipantId participant,
            Guid transactionId,
            DateTime timeStamp,
            TransactionalStatus status)
        {
            TransactionAgentCancelCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class NeverOverloaded : ITransactionOverloadDetector
    {
        public bool IsOverloaded() => false;
    }

    private sealed class TestClock : IClock
    {
        public DateTime UtcNow() => new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
    }

    private sealed class TestActivationLifetime : IActivationLifetime
    {
        private readonly CancellationTokenSource cancellation = new();

        public CancellationToken OnDeactivating => cancellation.Token;

        public IDisposable BlockDeactivation() => NullDisposable.Instance;

        public void Cancel() => cancellation.Cancel();
    }

    private sealed class TestGrainReference(GrainId grainId)
        : GrainReference(
            new GrainReferenceShared(
                grainId.Type,
                default,
                interfaceVersion: 0,
                runtime: null!,
                invokeMethodOptions: default,
                codecProvider: null!,
                copyContextPool: null!,
                serviceProvider: null!),
            grainId.Key);

    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class RecordingObserver(Guid transactionId) : IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>
    {
        private readonly ConcurrentQueue<TransactionDiagnosticEvents.TransactionDiagnosticEvent> events = new();

        public IReadOnlyList<TransactionDiagnosticEvents.TransactionDiagnosticEvent> Events => events.ToArray();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(TransactionDiagnosticEvents.TransactionDiagnosticEvent value)
        {
            if (value is TransactionDiagnosticEvents.TransactionEvent transactionEvent
                && transactionEvent.TransactionId == transactionId)
            {
                events.Enqueue(value);
            }
        }
    }
}
