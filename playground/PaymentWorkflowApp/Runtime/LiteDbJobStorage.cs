using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using LiteDB;
using Orleans.Serialization;

namespace PaymentWorkflowApp.Runtime;

public sealed class LiteDbJobStorage(Serializer<JobTaskState> serializer, DeepCopier<JobTaskState> copier) : IJobStorage, IDisposable
{
    private readonly Serializer<JobTaskState> _serializer = serializer;
    private readonly DeepCopier<JobTaskState> _copier = copier;
    private readonly object _lock = new();

    private readonly LiteDatabase _db = new(@"jobs.db");
    private readonly HashSet<TaskId> _removed = [];
    private Dictionary<TaskId, JobTaskState> _workingCopy = [];

    public IEnumerable<(TaskId Id, JobTaskState State)> Tasks
    {
        get
        {
            lock (_lock)
            {
                return _workingCopy.Select(static pair => (pair.Key, pair.Value)).ToList();
            }
        }
    }

    public void AddOrUpdateTask(TaskId taskId, JobTaskState state)
    {
        lock (_lock)
        {
            _workingCopy[taskId] = CopyState(state);
        }
    }
    public bool RemoveTask(TaskId taskId)
    {
        lock (_lock)
        {
            if (_workingCopy.Remove(taskId))
            {
                return _removed.Add(taskId);
            }
            return false;
        }
    }

    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out JobTaskState? state)
    {
        lock (_lock)
        {
            if (_workingCopy.TryGetValue(taskId, out var internalState))
            {
                state = CopyState(internalState);
                return true;
            }
        }

        state = null;
        return false;
    }

    public ValueTask ReadAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var collection = _db.GetCollection<JobEntity>("jobs");

            _workingCopy = [];
            foreach (var entry in collection.FindAll())
            {
                var taskId = TaskId.Parse(entry.Id ?? throw new InvalidDataException("The stored job id is missing."));
                var payload = entry.Payload ?? throw new InvalidDataException($"The payload for job '{taskId}' is missing.");
                var state = _serializer.Deserialize(payload)
                    ?? throw new InvalidDataException($"The payload for job '{taskId}' deserialized to null.");
                _workingCopy.Add(taskId, state);
            }
        }

        return default;
    }

    private JobTaskState CopyState(JobTaskState state) =>
        _copier.Copy(state)
        ?? throw new InvalidOperationException("The job state copier returned null.");

    public ValueTask WriteAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var collection = _db.GetCollection<JobEntity>("jobs");
            var entries = _workingCopy.Select(pair => new JobEntity
            {
                Id = pair.Key.ToString(),
                Payload = _serializer.SerializeToArray(pair.Value),
            }).ToList();

            _db.BeginTrans();
            try
            {
                foreach (var entry in entries)
                {
                    collection.Upsert(entry);
                }

                foreach (var id in _removed)
                {
                    collection.Delete(id.ToString());
                }

                _db.Commit();
                _removed.Clear();
            }
            catch
            {
                _db.Rollback();
                throw;
            }
        }

        return default;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _db.Dispose();
        }
    }

    private class JobEntity
    {
        [BsonField("_id")]
        public string? Id { get; set; }

        [BsonField("data")]
        public byte[]? Payload { get; set; }
    }
}
