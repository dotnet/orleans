using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using LiteDB;
using Orleans.Serialization;

namespace PaymentWorkflowApp.Runtime;

public sealed class LiteDbJobStorage(Serializer<JobTaskState> serializer, DeepCopier<JobTaskState> copier) : IJobStorage
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
            _workingCopy[taskId] = _copier.Copy(state);
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
                state = _copier.Copy(internalState);
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
                var taskId = TaskId.Parse(entry.Id!);

                _workingCopy.Add(taskId, _serializer.Deserialize(entry.Payload));
            }
        }

        return default;
    }

    public ValueTask WriteAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var collection = _db.GetCollection<JobEntity>("jobs");
            foreach (var (id, task) in _workingCopy)
            {
                var entry = new JobEntity
                {
                    Id = id.ToString(),
                    Payload = _serializer.SerializeToArray(task),
                };

                collection.Upsert(entry);
            }

            foreach (var id in _removed)
            {
                collection.Delete(id.ToString());
            }

            _removed.Clear();
        }

        return default;
    }

    private class JobEntity
    {
        [BsonField("_id")]
        public string? Id { get; set; }

        [BsonField("data")]
        public byte[]? Payload { get; set; }
    }
}
