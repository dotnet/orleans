using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using System.Runtime.CompilerServices;
using Orleans.DurableTasks;
using Orleans.Journaling;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;

namespace WorkflowsApp.Service;

internal sealed class DurableTaskGrainStorage : IDurableTaskGrainStorage, IDurableStateMachine
{
    private const byte VersionByte = 0;
    private readonly DurableTaskGrainStorageShared _shared;
    private readonly IStateMachineManager _stateMachineManager;
    private readonly Dictionary<TaskId, DurableTaskState> _items = [];
    private IStateMachineLogWriter? _storage;

    public DurableTaskGrainStorage(IStateMachineManager stateMachineManager, DurableTaskGrainStorageShared shared)
    {
        _shared = shared;
        _stateMachineManager = stateMachineManager;
        _stateMachineManager.RegisterStateMachine("$tasks", this);
    }

    public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks => _items.Select(static pair => (pair.Key, (IDurableTaskState)pair.Value));

    public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId parentId) => _items.Where(pair => parentId.IsParentOf(pair.Key)).Select(pair => (pair.Key, (IDurableTaskState)pair.Value));

    public bool RemoveTask(TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (ApplyRemoveTask(taskId))
        {
            GetStorage().AppendEntry(static (state, bufferWriter) =>
            {
                var (shared, taskId) = state;
                using var session = shared.SerializerSessionPool.GetSession();
                var writer = Writer.Create(bufferWriter, session);
                writer.WriteByte(VersionByte);
                writer.WriteVarUInt32((uint)CommandType.RemoveTask);
                shared.KeyCodec.WriteField(ref writer, 0, typeof(TaskId), taskId);
                writer.Commit();
            }, (_shared, taskId));
            return true;
        }

        return false;
    }

    public void RequestCancellation(TaskId taskId, IDurableTaskState state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (ApplyRequestTaskCancellation(taskId))
        {
            GetStorage().AppendEntry(static (state, bufferWriter) =>
            {
                var (shared, taskId) = state;
                using var session = shared.SerializerSessionPool.GetSession();
                var writer = Writer.Create(bufferWriter, session);
                writer.WriteByte(VersionByte);
                writer.WriteVarUInt32((uint)CommandType.RequestTaskCancellation);
                shared.KeyCodec.WriteField(ref writer, 0, typeof(TaskId), taskId);
                writer.Commit();
            }, (_shared, taskId));
        }
    }

    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out IDurableTaskState? state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (_items.TryGetValue(taskId, out var internalState))
        {
            state = internalState;
            return true;
        }

        state = null;
        return false;
    }

    public ValueTask ReadAsync(CancellationToken cancellationToken) => _stateMachineManager.InitializeAsync(cancellationToken);

    public ValueTask WriteAsync(CancellationToken cancellationToken) => _stateMachineManager.WriteStateAsync(cancellationToken);

    public IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (!TryGetTask(taskId, out var result))
        {
            result = CreateTask(taskId, request!);
        }

        return result;
    }

    private DurableTaskState CreateTask(TaskId taskId, IDurableTaskRequest request)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        var createdAt = _shared.TimeProvider.GetUtcNow();
        var result = ApplyCreateTask(taskId, request!, createdAt);
        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            var (shared, key, request, completedAt) = state;
            using var session = shared.SerializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.CreateTask);
            shared.KeyCodec.WriteField(ref writer, 0, typeof(TaskId), key);
            shared.RequestCodec.WriteField(ref writer, 1, typeof(IDurableTaskRequest), request);
            shared.DateTimeOffsetCodec.WriteField(ref writer, 1, typeof(DateTimeOffset), completedAt);
            writer.Commit();
        },
        (_shared, taskId, request, createdAt));
        return result;
    }

    public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        var completedAt = _shared.TimeProvider.GetUtcNow();
        ApplySetResultCore(CastState(state), response, completedAt);

        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            var (shared, taskId, response, completedAt) = state;
            using var session = shared.SerializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.SetResult);
            shared.KeyCodec.WriteField(ref writer, 0, typeof(TaskId), taskId);
            shared.ResponseCodec.WriteField(ref writer, 1, typeof(DurableTaskResponse), response);
            shared.DateTimeOffsetCodec.WriteField(ref writer, 1, typeof(DateTimeOffset), completedAt);
            writer.Commit();
        },
        (_shared, taskId, response, completedAt));
    }

    public void AddObserver(TaskId taskId, IDurableTaskState state, IDurableTaskObserver observer)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (ApplyAddObserverCore(CastState(state), observer))
        {
            GetStorage().AppendEntry(static (state, bufferWriter) =>
            {
                var (shared, taskId, observer) = state;
                using var session = shared.SerializerSessionPool.GetSession();
                var writer = Writer.Create(bufferWriter, session);
                writer.WriteByte(VersionByte);
                writer.WriteVarUInt32((uint)CommandType.AddObserver);
                shared.KeyCodec.WriteField(ref writer, 0, typeof(TaskId), taskId);
                shared.ObserverCodec.WriteField(ref writer, 0, typeof(IDurableTaskObserver), observer);
                writer.Commit();
            },
            (_shared, taskId, observer));
        }
    }

    public void ClearObservers(TaskId taskId, IDurableTaskState state)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (ApplyClearObserversCore(CastState(state)))
        {
            GetStorage().AppendEntry(static (state, bufferWriter) =>
            {
                var (shared, taskId) = state;
                using var session = shared.SerializerSessionPool.GetSession();
                var writer = Writer.Create(bufferWriter, session);
                writer.WriteByte(VersionByte);
                writer.WriteVarUInt32((uint)CommandType.ClearObservers);
                shared.KeyCodec.WriteField(ref writer, 0, typeof(TaskId), taskId);
                writer.Commit();
            }, (_shared, taskId));
        }
    }

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage)
    {
        _items.Clear();
        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        using var session = _shared.SerializerSessionPool.GetSession();
        var reader = Reader.Create(logEntry, session);
        var version = reader.ReadByte();
        if (version != VersionByte)
        {
            throw new NotSupportedException($"This instance of {nameof(DurableTaskGrainStorage)} supports version {(uint)VersionByte} and not version {(uint)version}.");
        }

        var commandType = (CommandType)reader.ReadVarUInt32();
        switch (commandType)
        {
            case CommandType.CreateTask:
                ApplyCreateTask(ReadKey(ref reader), ReadRequest(ref reader), ReadDateTimeOffset(ref reader));
                break;
            case CommandType.SetResult:
                ApplySetResult(ReadKey(ref reader), ReadResponse(ref reader), ReadDateTimeOffset(ref reader));
                break;
            case CommandType.AddObserver:
                ApplyAddObserver(ReadKey(ref reader), ReadObserver(ref reader));
                break;
            case CommandType.ClearObservers:
                ApplyClearObservers(ReadKey(ref reader));
                break;
            case CommandType.RemoveTask:
                ApplyRemoveTask(ReadKey(ref reader));
                break;
            case CommandType.RequestTaskCancellation:
                ApplyRequestTaskCancellation(ReadKey(ref reader));
                break;
            case CommandType.Clear:
                ApplyClear();
                break;
            case CommandType.Snapshot:
                ApplySnapshot(ref reader);
                break;
            default:
                throw new NotSupportedException($"Command type {commandType} is not supported");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        TaskId ReadKey(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            return _shared.KeyCodec.ReadValue(ref reader, field);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        DurableTaskState ReadValue(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            return _shared.ValueCodec.ReadValue(ref reader, field);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        IDurableTaskObserver ReadObserver(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            return _shared.ObserverCodec.ReadValue(ref reader, field);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        DurableTaskResponse ReadResponse(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            return _shared.ResponseCodec.ReadValue(ref reader, field);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        IDurableTaskRequest ReadRequest(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            return _shared.RequestCodec.ReadValue(ref reader, field);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        DateTimeOffset ReadDateTimeOffset(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            return _shared.DateTimeOffsetCodec.ReadValue(ref reader, field);
        }

        void ApplySnapshot(ref Reader<ReadOnlySequenceInput> reader)
        {
            var count = reader.ReadVarInt32();
            _items.Clear();
            _items.EnsureCapacity(count);
            for (var i = 0; i < count; i++)
            {
                var key = ReadKey(ref reader);
                var value = ReadValue(ref reader);
                _items[key] = value;
            }
        }
    }

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter)
    {
        snapshotWriter.AppendEntry(static (self, bufferWriter) =>
        {
            var shared = self._shared;
            using var session = shared.SerializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.Snapshot);
            writer.WriteVarInt32(self._items.Count);
            foreach (var (key, value) in self._items)
            {
                shared.KeyCodec.WriteField(ref writer, 0, typeof(TaskId), key);
                shared.ValueCodec.WriteField(ref writer, 0, typeof(DurableTaskState), value);
            }

            writer.Commit();
        }, this);
    }

    public void Clear()
    {
        ApplyClear();
        GetStorage().AppendEntry(static (shared, bufferWriter) =>
        {
            using var session = shared.SerializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.Clear);
            writer.Commit();
        },
        _shared);
    }

    public bool Contains(TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        return _items.ContainsKey(taskId);
    }

    private DurableTaskState ApplyCreateTask(TaskId taskId, IDurableTaskRequest request, DateTimeOffset createdAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        var result = new DurableTaskState
        {
            Request = request,
            CreatedAt = createdAt,
        };
        _items.Add(taskId, result);
        return result;
    }

    private void ApplySetResult(TaskId taskId, DurableTaskResponse result, DateTimeOffset completedAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ApplySetResultCore(_items[taskId], result, completedAt);
    }

    private static void ApplySetResultCore(DurableTaskState task, DurableTaskResponse result, DateTimeOffset completedAt)
    {
        Debug.Assert(task.Result is null);
        Debug.Assert(task.CompletedAt is null);
        task.Result = result;
        task.CompletedAt = completedAt;
    }

    private bool ApplyAddObserver(TaskId taskId, IDurableTaskObserver observer)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        return ApplyAddObserverCore(_items[taskId], observer);
    }

    private static bool ApplyAddObserverCore(DurableTaskState state, IDurableTaskObserver observer)
    {
        var observers = state.Observers ??= [];
        return observers.Add(observer);
    }

    private void ApplyClearObservers(TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ApplyClearObserversCore(_items[taskId]);
    }

    private static bool ApplyClearObserversCore(DurableTaskState state)
    {
        if (state.Observers is { Count: > 0 } observers)
        {
            observers.Clear();
            return true;
        }

        return false;
    }

    private bool ApplyRemoveTask(TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        return _items.Remove(taskId);
    }
    private void ApplyClear() => _items.Clear();
    private bool ApplyRequestTaskCancellation(TaskId taskId)
    {
        if (!_items.TryGetValue(taskId, out var taskState))
        {
            return false;
        }

        if (taskState.CancellationRequestedAt.HasValue || taskState.CompletedAt.HasValue)
        {
            return false;
        }

        taskState.CancellationRequestedAt = _shared.TimeProvider.GetUtcNow();
        return true;
    }

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private IStateMachineLogWriter GetStorage()
    {
        Debug.Assert(_storage is not null);
        return _storage;
    }

    private static DurableTaskState CastState(IDurableTaskState state)
    {
        if (state is not DurableTaskState result)
        {
            throw new ArgumentException("The provided value does not belong to this storage provider", nameof(state));
        }

        return result;
    }

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();

    private enum CommandType
    {
        CreateTask,
        SetResult,
        AddObserver,
        ClearObservers,
        RemoveTask,
        RequestTaskCancellation,
        Clear,
        Snapshot
    }
}
