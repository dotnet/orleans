using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using Orleans.Serialization;
namespace PaymentWorkflowApp.Runtime;

internal class VolatileJobStorage(DeepCopier<Dictionary<TaskId, JobTaskState>> storageCopier, DeepCopier<JobTaskState> stateCopier) : IJobStorage
{
    private Dictionary<TaskId, JobTaskState> _workingCopy = [];
    private Dictionary<TaskId, JobTaskState> _persistedCopy = [];
    private readonly DeepCopier<Dictionary<TaskId, JobTaskState>> _storageCopier = storageCopier;
    private readonly DeepCopier<JobTaskState> _stateCopier = stateCopier;

    public IEnumerable<(TaskId Id, JobTaskState State)> Tasks => _workingCopy.Select(static pair => (pair.Key, pair.Value));

    public void AddOrUpdateTask(TaskId taskId, JobTaskState state) => _workingCopy[taskId] = _stateCopier.Copy(state);
    public bool RemoveTask(TaskId taskId) => _workingCopy.Remove(taskId);
    public bool TryGetTask(TaskId taskId, [NotNullWhen(true)] out JobTaskState? state)
    {
        if (_workingCopy.TryGetValue(taskId, out var internalState))
        {
            state = _stateCopier.Copy(internalState);
            return true;
        }

        state = null;
        return false;
    }

    public ValueTask ReadAsync(CancellationToken cancellationToken)
    {
        _workingCopy = _storageCopier.Copy(_persistedCopy);
        return default;
    }

    public ValueTask WriteAsync(CancellationToken cancellationToken)
    {
        _persistedCopy = _storageCopier.Copy(_workingCopy);
        return default;
    }
}
