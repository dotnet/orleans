using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace UnitTests.Storage;

[TestCategory("BVT"), TestCategory("Storage")]
public class StateStorageBridgeConcurrencyTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StateProperties_UseBridgeGrainStateAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var replacementState = new TestState { Value = "replacement" };

        storage.ReadAsync = grainState =>
        {
            grainState.ETag = "etag-1";
            grainState.RecordExists = true;
            return Task.CompletedTask;
        };

        await RunInGrainContextAsync(context, async () =>
        {
            bridge.State = new TestState { Value = "initial" };

            await bridge.ReadStateAsync();
            bridge.State = replacementState;

            Assert.Same(replacementState, bridge.State);
            Assert.Equal("etag-1", bridge.Etag);
            Assert.True(bridge.RecordExists);
        });
    }

    [Fact]
    public async Task WriteStateAsync_NoContention_WritesStorageAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);

        await RunInGrainContextAsync(context, () => bridge.WriteStateAsync());

        Assert.Equal(1, storage.WriteCallCount);
        Assert.Equal(0, storage.ReadCallCount);
        Assert.Equal(0, storage.ClearCallCount);
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task ReadStateAsync_NoPredecessor_ReadsStorageAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);

        await RunInGrainContextAsync(context, () => bridge.ReadStateAsync());

        Assert.Equal(1, storage.ReadCallCount);
        Assert.Equal(0, storage.WriteCallCount);
        Assert.Equal(0, storage.ClearCallCount);
    }

    [Fact]
    public async Task ClearStateAsync_NoContention_ClearsStorageAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);

        await RunInGrainContextAsync(context, () => bridge.ClearStateAsync());

        Assert.Equal(1, storage.ClearCallCount);
        Assert.Equal(0, storage.ReadCallCount);
        Assert.Equal(0, storage.WriteCallCount);
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task WriteStateAsync_QueuedBeforeWriteStarts_CoalescesWritesAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var writeCompletion = CreateCompletionSource();
        storage.WriteAsync = _ => writeCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var firstWrite = bridge.WriteStateAsync();
            var secondWrite = bridge.WriteStateAsync();

            Assert.Same(firstWrite, secondWrite);
            await WaitUntilAsync(() => storage.WriteCallCount == 1);
            Assert.False(firstWrite.IsCompleted);

            writeCompletion.SetResult();
            await firstWrite;
        });

        Assert.Equal(1, storage.WriteCallCount);
        Assert.Single(storage.EtagSnapshot());
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task WriteStateAsync_QueuedAfterWriteStarts_PerformsSecondWriteAfterFirstCompletesAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var firstWriteCompletion = CreateCompletionSource();
        var secondWriteCompletion = CreateCompletionSource();
        storage.WriteAsync = _ => storage.WriteCallCount == 1 ? firstWriteCompletion.Task : secondWriteCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var firstWrite = bridge.WriteStateAsync();
            await WaitUntilAsync(() => storage.WriteCallCount == 1);
            var secondWrite = bridge.WriteStateAsync();

            Assert.NotSame(firstWrite, secondWrite);
            Assert.Equal(1, storage.WriteCallCount);
            Assert.False(secondWrite.IsCompleted);

            firstWriteCompletion.SetResult();
            await WaitUntilAsync(() => storage.WriteCallCount == 2);
            Assert.Equal(["write-1", "write-2"], storage.Snapshot());

            secondWriteCompletion.SetResult();
            await Task.WhenAll(firstWrite, secondWrite);
        });

        var etags = storage.EtagSnapshot();
        Assert.Equal(2, etags.Length);
        Assert.NotEqual(etags[0], etags[1]);
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task ReadStateAsync_AfterSuccessfulWrite_WaitsForWriteAndDoesNotReadStorageAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var writeCompletion = CreateCompletionSource();
        storage.WriteAsync = _ => writeCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var writeTask = bridge.WriteStateAsync();
            await WaitUntilAsync(() => storage.WriteCallCount == 1);
            var readTask = bridge.ReadStateAsync();

            Assert.False(readTask.IsCompleted);

            writeCompletion.SetResult();
            await Task.WhenAll(writeTask, readTask);
        });

        Assert.Equal(1, storage.WriteCallCount);
        Assert.Equal(0, storage.ReadCallCount);
        Assert.True(bridge.RecordExists);
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task ReadStateAsync_SatisfiedByWrite_EnforcesRuntimeContextAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var writeCompletion = CreateCompletionSource();
        storage.WriteAsync = _ => writeCompletion.Task;

        var writeTask = RunInGrainContextAsync(context, () => bridge.WriteStateAsync());
        await WaitUntilAsync(() => storage.WriteCallCount == 1);
        var readTask = bridge.ReadStateAsync();

        writeCompletion.SetResult();
        await writeTask;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => readTask);
        Assert.Contains("Activation access violation", exception.Message);
    }

    [Fact]
    public async Task ReadStateAsync_AfterFailedWrite_PerformsReadAfterWriteFailsAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var writeCompletion = CreateCompletionSource();
        var readCompletion = CreateCompletionSource();
        var writeFailure = new InvalidOperationException("write failed");
        storage.WriteAsync = _ => writeCompletion.Task;
        storage.ReadAsync = async grainState =>
        {
            await readCompletion.Task;
            grainState.ETag = "etag-read-after-failed-write";
            grainState.RecordExists = true;
        };

        await RunInGrainContextAsync(context, async () =>
        {
            var writeTask = bridge.WriteStateAsync();
            await WaitUntilAsync(() => storage.WriteCallCount == 1);
            var readTask = bridge.ReadStateAsync();
            writeCompletion.SetException(writeFailure);
            await WaitUntilAsync(() => storage.ReadCallCount == 1);

            Assert.False(readTask.IsCompleted);

            readCompletion.SetResult();
            await readTask;
            var exception = await Assert.ThrowsAsync<OrleansException>(() => writeTask);
            Assert.Same(writeFailure, exception.InnerException);
        });

        Assert.Equal(1, storage.WriteCallCount);
        Assert.Equal(1, storage.ReadCallCount);
        Assert.Empty(storage.EtagSnapshot());
        Assert.Equal("etag-read-after-failed-write", bridge.Etag);
    }

    [Fact]
    public async Task WriteStateAsync_AfterReadQueuedBehindFailedWrite_WaitsForReadBeforeWritingAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var firstWriteCompletion = CreateCompletionSource();
        var readCompletion = CreateCompletionSource();
        var secondWriteCompletion = CreateCompletionSource();
        storage.WriteAsync = _ => storage.WriteCallCount == 1 ? firstWriteCompletion.Task : secondWriteCompletion.Task;
        storage.ReadAsync = async grainState =>
        {
            await readCompletion.Task;
            grainState.ETag = "etag-read-recovery";
            grainState.RecordExists = true;
        };

        await RunInGrainContextAsync(context, async () =>
        {
            var firstWrite = bridge.WriteStateAsync();
            await WaitUntilAsync(() => storage.WriteCallCount == 1);
            var read = bridge.ReadStateAsync();
            var secondWrite = bridge.WriteStateAsync();
            firstWriteCompletion.SetException(new InvalidOperationException("write failed"));
            await WaitUntilAsync(() => storage.ReadCallCount == 1);

            Assert.Equal(1, storage.WriteCallCount);
            Assert.False(secondWrite.IsCompleted);

            readCompletion.SetResult();
            await WaitUntilAsync(() => storage.WriteCallCount == 2);
            Assert.Equal(["write-1", "read", "write-2"], storage.Snapshot());

            secondWriteCompletion.SetResult();
            await Task.WhenAll(read, secondWrite);
            await Assert.ThrowsAsync<OrleansException>(() => firstWrite);
        });

        var etag = Assert.Single(storage.EtagSnapshot());
        Assert.StartsWith("etag-", etag);
        Assert.NotEqual("etag-read-recovery", etag);
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task ClearStateAsync_QueuedBeforeClearStarts_CoalescesClearsAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var clearCompletion = CreateCompletionSource();
        storage.ClearAsync = _ => clearCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var firstClear = bridge.ClearStateAsync();
            var secondClear = bridge.ClearStateAsync();

            Assert.Same(firstClear, secondClear);
            await WaitUntilAsync(() => storage.ClearCallCount == 1);

            clearCompletion.SetResult();
            await firstClear;
        });

        Assert.Equal(1, storage.ClearCallCount);
        Assert.Single(storage.EtagSnapshot());
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task ClearStateAsync_AfterSuccessfulClear_WaitsForClearAndDoesNotClearAgainAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var clearCompletion = CreateCompletionSource();
        storage.ClearAsync = _ => clearCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var firstClear = bridge.ClearStateAsync();
            await WaitUntilAsync(() => storage.ClearCallCount == 1);
            var secondClear = bridge.ClearStateAsync();

            Assert.False(secondClear.IsCompleted);

            clearCompletion.SetResult();
            await Task.WhenAll(firstClear, secondClear);
        });

        Assert.Equal(1, storage.ClearCallCount);
        Assert.Single(storage.EtagSnapshot());
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task ClearStateAsync_AfterFailedClear_PerformsSecondClearAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var firstClearCompletion = CreateCompletionSource();
        var secondClearCompletion = CreateCompletionSource();
        var clearFailure = new InvalidOperationException("clear failed");
        storage.ClearAsync = _ => storage.ClearCallCount == 1 ? firstClearCompletion.Task : secondClearCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var firstClear = bridge.ClearStateAsync();
            await WaitUntilAsync(() => storage.ClearCallCount == 1);
            var secondClear = bridge.ClearStateAsync();
            firstClearCompletion.SetException(clearFailure);
            await WaitUntilAsync(() => storage.ClearCallCount == 2);

            Assert.False(secondClear.IsCompleted);

            secondClearCompletion.SetResult();
            await secondClear;
            var exception = await Assert.ThrowsAsync<OrleansException>(() => firstClear);
            Assert.Same(clearFailure, exception.InnerException);
        });

        Assert.Equal(2, storage.ClearCallCount);
        Assert.Single(storage.EtagSnapshot());
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task ReadStateAsync_AfterSuccessfulClear_WaitsForClearAndDoesNotReadStorageAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var clearCompletion = CreateCompletionSource();
        storage.ClearAsync = _ => clearCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var clearTask = bridge.ClearStateAsync();
            await WaitUntilAsync(() => storage.ClearCallCount == 1);
            var readTask = bridge.ReadStateAsync();

            Assert.False(readTask.IsCompleted);

            clearCompletion.SetResult();
            await Task.WhenAll(clearTask, readTask);
        });

        Assert.Equal(1, storage.ClearCallCount);
        Assert.Equal(0, storage.ReadCallCount);
        Assert.False(bridge.RecordExists);
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task ClearStateAsync_SatisfiedByClear_EnforcesRuntimeContextAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var clearCompletion = CreateCompletionSource();
        storage.ClearAsync = _ => clearCompletion.Task;

        var firstClear = RunInGrainContextAsync(context, () => bridge.ClearStateAsync());
        await WaitUntilAsync(() => storage.ClearCallCount == 1);
        var secondClear = bridge.ClearStateAsync();

        clearCompletion.SetResult();
        await firstClear;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => secondClear);
        Assert.Contains("Activation access violation", exception.Message);
    }

    [Fact]
    public async Task ReadStateAsync_AfterFailedClear_PerformsReadAfterClearFailsAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var clearCompletion = CreateCompletionSource();
        var readCompletion = CreateCompletionSource();
        var clearFailure = new InvalidOperationException("clear failed");
        storage.ClearAsync = _ => clearCompletion.Task;
        storage.ReadAsync = async grainState =>
        {
            await readCompletion.Task;
            grainState.ETag = "etag-read-after-failed-clear";
            grainState.RecordExists = true;
        };

        await RunInGrainContextAsync(context, async () =>
        {
            var clearTask = bridge.ClearStateAsync();
            await WaitUntilAsync(() => storage.ClearCallCount == 1);
            var readTask = bridge.ReadStateAsync();
            clearCompletion.SetException(clearFailure);
            await WaitUntilAsync(() => storage.ReadCallCount == 1);

            Assert.False(readTask.IsCompleted);

            readCompletion.SetResult();
            await readTask;
            var exception = await Assert.ThrowsAsync<OrleansException>(() => clearTask);
            Assert.Same(clearFailure, exception.InnerException);
        });

        Assert.Equal(1, storage.ClearCallCount);
        Assert.Equal(1, storage.ReadCallCount);
        Assert.Empty(storage.EtagSnapshot());
        Assert.Equal("etag-read-after-failed-clear", bridge.Etag);
    }

    [Fact]
    public async Task WriteStateAsync_AfterRead_WaitsForReadBeforeWritingAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var readCompletion = CreateCompletionSource();
        var writeCompletion = CreateCompletionSource();
        storage.ReadAsync = async grainState =>
        {
            await readCompletion.Task;
            grainState.ETag = "etag-initial-read";
            grainState.RecordExists = true;
        };
        storage.WriteAsync = _ => writeCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var readTask = bridge.ReadStateAsync();
            await WaitUntilAsync(() => storage.ReadCallCount == 1);
            var writeTask = bridge.WriteStateAsync();

            Assert.Equal(0, storage.WriteCallCount);
            Assert.False(writeTask.IsCompleted);

            readCompletion.SetResult();
            await WaitUntilAsync(() => storage.WriteCallCount == 1);
            Assert.Equal(["read", "write-1"], storage.Snapshot());

            writeCompletion.SetResult();
            await Task.WhenAll(readTask, writeTask);
        });

        var writeEtag = AssertLatestEtag(bridge, storage);
        Assert.NotEqual("etag-initial-read", writeEtag);
    }

    [Fact]
    public async Task ReadStateAsync_AfterSuccessfulRead_WaitsForReadAndDoesNotReadAgainAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var readCompletion = CreateCompletionSource();
        storage.ReadAsync = async grainState =>
        {
            await readCompletion.Task;
            grainState.ETag = "etag-read";
            grainState.RecordExists = true;
        };

        await RunInGrainContextAsync(context, async () =>
        {
            var firstRead = bridge.ReadStateAsync();
            await WaitUntilAsync(() => storage.ReadCallCount == 1);
            var secondRead = bridge.ReadStateAsync();

            Assert.False(secondRead.IsCompleted);

            readCompletion.SetResult();
            await Task.WhenAll(firstRead, secondRead);
        });

        Assert.Equal(1, storage.ReadCallCount);
        Assert.Equal("etag-read", bridge.Etag);
    }

    [Fact]
    public async Task MultipleReadsAfterPendingWrite_AreSatisfiedBySuccessfulWriteWithoutReadingAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        var bridge = CreateBridge(context, storage);
        var writeCompletion = CreateCompletionSource();
        storage.WriteAsync = _ => writeCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var writeTask = bridge.WriteStateAsync();
            await WaitUntilAsync(() => storage.WriteCallCount == 1);
            var firstRead = bridge.ReadStateAsync();
            var secondRead = bridge.ReadStateAsync();

            Assert.False(firstRead.IsCompleted);
            Assert.False(secondRead.IsCompleted);

            writeCompletion.SetResult();
            await Task.WhenAll(writeTask, firstRead, secondRead);
        });

        Assert.Equal(1, storage.WriteCallCount);
        Assert.Equal(0, storage.ReadCallCount);
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task CanceledQueuedWrite_ReachesProviderAfterPredecessorAndPropagatesCancellationAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        IStorage bridge = CreateBridge(context, storage);
        var readCompletion = CreateCompletionSource();
        using var cancellationTokenSource = new CancellationTokenSource();
        storage.ReadAsync = _ => readCompletion.Task;
        storage.WriteWithCancellationAsync = (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await RunInGrainContextAsync(context, async () =>
        {
            var readTask = bridge.ReadStateAsync();
            await WaitUntilAsync(() => storage.ReadCallCount == 1);
            var canceledWriteWaiter = bridge.WriteStateAsync(cancellationTokenSource.Token);
            await cancellationTokenSource.CancelAsync();

            Assert.False(canceledWriteWaiter.IsCompleted);
            Assert.Equal(0, storage.WriteCallCount);

            readCompletion.SetResult();
            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => canceledWriteWaiter);
            Assert.Equal(cancellationTokenSource.Token, exception.CancellationToken);
            await readTask;
        });

        Assert.Equal(1, storage.WriteCallCount);
    }

    [Fact]
    public async Task CancelableWrite_DoesNotCoalesceWithUncancelableWriteAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        IStorage bridge = CreateBridge(context, storage);
        var readCompletion = CreateCompletionSource();
        using var cancellationTokenSource = new CancellationTokenSource();
        storage.ReadAsync = _ => readCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var readTask = bridge.ReadStateAsync();
            await WaitUntilAsync(() => storage.ReadCallCount == 1);
            var cancelableWrite = bridge.WriteStateAsync(cancellationTokenSource.Token);
            var uncancelableWrite = bridge.WriteStateAsync();

            readCompletion.SetResult();
            await Task.WhenAll(readTask, cancelableWrite, uncancelableWrite);
        });

        Assert.Equal(2, storage.WriteCallCount);
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task InterfaceMethods_UseConcurrencySafeBridgeAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        IStorage<TestState> bridge = CreateBridge(context, storage);
        var writeCompletion = CreateCompletionSource();
        storage.WriteAsync = _ => writeCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var firstWrite = bridge.WriteStateAsync();
            var secondWrite = bridge.WriteStateAsync();

            Assert.Same(firstWrite, secondWrite);
            await WaitUntilAsync(() => storage.WriteCallCount == 1);

            writeCompletion.SetResult();
            await Task.WhenAll(firstWrite, secondWrite);
        });

        Assert.Equal(1, storage.WriteCallCount);
        AssertLatestEtag(bridge, storage);
    }

    [Fact]
    public async Task PersistentStateInterfaceMethods_UseConcurrencySafeBridgeAsync()
    {
        using var context = TestGrainContext.Create();
        var storage = new ControllableGrainStorage();
        IPersistentState<TestState> persistentState = new PersistentState<TestState>("state", context, storage);
        var writeCompletion = CreateCompletionSource();
        storage.WriteAsync = _ => writeCompletion.Task;

        await RunInGrainContextAsync(context, async () =>
        {
            var firstWrite = persistentState.WriteStateAsync();
            var secondWrite = persistentState.WriteStateAsync();

            Assert.Same(firstWrite, secondWrite);
            await WaitUntilAsync(() => storage.WriteCallCount == 1);

            writeCompletion.SetResult();
            await Task.WhenAll(firstWrite, secondWrite);
        });

        Assert.Equal(1, storage.WriteCallCount);
        AssertLatestEtag(persistentState, storage);
    }

    private static StateStorageBridge<TestState> CreateBridge(TestGrainContext context, ControllableGrainStorage storage)
        => new("state", context, storage);

    private static string AssertLatestEtag(IStorage storageAccessor, ControllableGrainStorage storage)
    {
        var etag = storage.LastEtag;
        Assert.StartsWith("etag-", etag);
        Assert.Equal(etag, storageAccessor.Etag);
        return etag;
    }

    private static TaskCompletionSource CreateCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task RunInGrainContextAsync(TestGrainContext context, Func<Task> action)
    {
        var task = Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.None,
            context.WorkItemGroup.TaskScheduler).Unwrap();

        return task.WaitAsync(WaitTimeout);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellationTokenSource = new CancellationTokenSource(WaitTimeout);

        while (!condition())
        {
            cancellationTokenSource.Token.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationTokenSource.Token);
        }
    }

    [GenerateSerializer]
    public sealed class TestState
    {
        [Id(0)]
        public string Value { get; set; } = null!;
    }

    private sealed class ControllableGrainStorage : IGrainStorage
    {
        private readonly object _gate = new();
        private readonly List<string> _operations = [];
        private readonly List<string> _etags = [];
        private int _readCallCount;
        private int _writeCallCount;
        private int _clearCallCount;
        private string _lastEtag = null!;

        public Func<IGrainState<TestState>, Task> ReadAsync { get; set; } = _ => Task.CompletedTask;

        public Func<IGrainState<TestState>, Task> WriteAsync { get; set; } = _ => Task.CompletedTask;

        public Func<IGrainState<TestState>, CancellationToken, Task>? WriteWithCancellationAsync { get; set; }

        public Func<IGrainState<TestState>, Task> ClearAsync { get; set; } = _ => Task.CompletedTask;

        public int ReadCallCount
        {
            get
            {
                lock (_gate)
                {
                    return _readCallCount;
                }
            }
        }

        public int WriteCallCount
        {
            get
            {
                lock (_gate)
                {
                    return _writeCallCount;
                }
            }
        }

        public int ClearCallCount
        {
            get
            {
                lock (_gate)
                {
                    return _clearCallCount;
                }
            }
        }

        public string LastEtag
        {
            get
            {
                lock (_gate)
                {
                    return _lastEtag;
                }
            }
        }

        public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var testState = GetTestState(grainState);

            lock (_gate)
            {
                _readCallCount++;
                _operations.Add("read");
            }

            await ReadAsync(testState);
        }

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => WriteStateAsync(stateName, grainId, grainState, CancellationToken.None);

        public async Task WriteStateAsync<T>(
            string stateName,
            GrainId grainId,
            IGrainState<T> grainState,
            CancellationToken cancellationToken)
        {
            var testState = GetTestState(grainState);

            lock (_gate)
            {
                _writeCallCount++;
                _operations.Add($"write-{_writeCallCount}");
            }

            await (WriteWithCancellationAsync is { } write
                ? write(testState, cancellationToken)
                : WriteAsync(testState));
            ResetEtag(testState, recordExists: true);
        }

        public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var testState = GetTestState(grainState);

            lock (_gate)
            {
                _clearCallCount++;
                _operations.Add($"clear-{_clearCallCount}");
            }

            await ClearAsync(testState);
            ResetEtag(testState, recordExists: false);
        }

        public string[] Snapshot()
        {
            lock (_gate)
            {
                return _operations.ToArray();
            }
        }

        public string[] EtagSnapshot()
        {
            lock (_gate)
            {
                return _etags.ToArray();
            }
        }

        private void ResetEtag(IGrainState<TestState> grainState, bool recordExists)
        {
            var etag = $"etag-{Guid.NewGuid():N}";
            grainState.ETag = etag;
            grainState.RecordExists = recordExists;

            lock (_gate)
            {
                _lastEtag = etag;
                _etags.Add(etag);
            }
        }

        private static IGrainState<TestState> GetTestState<T>(IGrainState<T> grainState)
        {
            if (grainState is IGrainState<TestState> testState)
            {
                return testState;
            }

            throw new InvalidOperationException($"Unexpected grain state type {typeof(T)}.");
        }
    }

    private sealed class TestGrainContext : IGrainContext, IDisposable
    {
        private ServiceProvider _activationServices = null!;

        private TestGrainContext()
        {
        }

        public static TestGrainContext Create()
        {
            var context = new TestGrainContext();
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddLogging();
            services.AddMetrics();
            services.AddSerializer();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<SchedulerInstruments>();
            services.AddSingleton<StorageInstruments>();
            services.AddSingleton<StateStorageBridgeSharedMap>();
            services.Configure<SchedulingOptions>(options =>
            {
                options.DelayWarningThreshold = TimeSpan.FromMilliseconds(100);
                options.ActivationSchedulingQuantum = TimeSpan.FromMilliseconds(100);
                options.TurnWarningLengthThreshold = TimeSpan.FromMilliseconds(100);
                options.StoppedActivationWarningInterval = TimeSpan.FromMilliseconds(200);
            });

            context._activationServices = services.BuildServiceProvider();
            var loggerFactory = context._activationServices.GetRequiredService<ILoggerFactory>();
            context.ObservableLifecycle = new GrainLifecycle(loggerFactory.CreateLogger<GrainLifecycle>());
            context.WorkItemGroup = new WorkItemGroup(
                context,
                context._activationServices.GetRequiredService<IOptions<SchedulingOptions>>(),
                context._activationServices.GetRequiredService<SchedulerInstruments>());

            return context;
        }

        public WorkItemGroup WorkItemGroup { get; private set; } = null!;

        public GrainReference GrainReference => throw new NotImplementedException();

        public GrainId GrainId { get; } = GrainId.Create("state-storage-bridge-test", Guid.NewGuid().ToString("N"));

        public IAddressable GrainInstance => throw new NotImplementedException();

        public ActivationId ActivationId => throw new NotImplementedException();

        public GrainAddress Address => throw new NotImplementedException();

        public IServiceProvider ActivationServices => _activationServices;

        public IDictionary<object, object> Items { get; } = new Dictionary<object, object>();

        public IGrainLifecycle ObservableLifecycle { get; private set; } = null!;

        public IWorkItemScheduler Scheduler => WorkItemGroup;

        public bool IsExemptFromCollection => false;

        public PlacementStrategy PlacementStrategy => throw new NotImplementedException();

        object IGrainContext.GrainInstance => throw new NotImplementedException();

        public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken)
        {
        }

        public Task Deactivated => Task.CompletedTask;

        public void Dispose()
        {
            (Scheduler as IDisposable)?.Dispose();
            _activationServices?.Dispose();
        }

        public object GetComponent(Type componentType) => throw new NotImplementedException();

        public object GetTarget() => throw new NotImplementedException();

        public void ReceiveMessage(object message) => throw new NotImplementedException();

        public void SetComponent<TComponent>(TComponent? value) where TComponent : class => throw new NotImplementedException();

        bool IEquatable<IGrainContext>.Equals(IGrainContext? other) => ReferenceEquals(this, other);

        void IGrainContext.Rehydrate(IRehydrationContext context) => throw new NotImplementedException();

        void IGrainContext.Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
