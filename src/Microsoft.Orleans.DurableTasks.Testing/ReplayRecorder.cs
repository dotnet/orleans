using Orleans.DurableTasks.Runtime;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Testing;

/// <summary>
/// Records what the replay silo's runtime reports about the replay of one grain at a time, and builds its result.
/// </summary>
internal sealed class ReplayRecorder : IDurableTaskReplayObserver
{
    private readonly object _lock = new();
    private Session? _session;

    /// <summary>Starts recording the replay of <paramref name="grainId"/>.</summary>
    public Session Begin(GrainId grainId)
    {
        lock (_lock)
        {
            return _session = new(grainId);
        }
    }

    /// <summary>Stops recording, so the deactivation which follows the replay changes nothing.</summary>
    public void End()
    {
        lock (_lock)
        {
            _session = null;
        }
    }

    public void OnRecovered(DurableTaskGrainRuntime runtime) => Find(runtime.GrainId)?.OnRecovered(runtime);

    public void OnAttemptStarted(GrainId grainId, TaskId executionId, IReadOnlyCollection<TaskId> recorded) =>
        Find(grainId)?.Update(executionId, execution => execution.Start(recorded));

    public void OnOperationReached(GrainId grainId, TaskId executionId, TaskId operationId) =>
        Find(grainId)?.Update(executionId, execution => execution.Reach(operationId));

    public void OnNewOperation(GrainId grainId, TaskId executionId, TaskId operationId, string description) =>
        Find(grainId)?.Update(executionId, execution => execution.Stop(operationId, description));

    public void OnWaitStarted(GrainId grainId, TaskId executionId, IReadOnlyCollection<TaskId> awaited) =>
        Find(grainId)?.Update(executionId, execution => execution.Waits.Add(awaited));

    public void OnWaitEnded(GrainId grainId, TaskId executionId, IReadOnlyCollection<TaskId> awaited) =>
        Find(grainId)?.Update(executionId, execution => execution.Waits.Remove(awaited));

    public void OnVersion(GrainId grainId, TaskId executionId, string name, int version, bool recorded) =>
        Find(grainId)?.Update(executionId, execution => execution.Versions[name] = version);

    public void OnAttemptEnded(
        GrainId grainId,
        TaskId executionId,
        DurableTaskResponse response,
        DurableTaskAttemptCanceledException? cancellation) =>
        Find(grainId)?.Update(executionId, execution => execution.End(response, cancellation));

    private Session? Find(GrainId grainId)
    {
        lock (_lock)
        {
            return _session is { } session && session.GrainId == grainId ? session : null;
        }
    }

    /// <summary>The replay of one grain.</summary>
    internal sealed class Session(GrainId grainId)
    {
        private readonly object _lock = new();
        private readonly List<Execution> _executions = [];
        private readonly TaskCompletionSource<DurableTaskGrainRuntime> _recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _completed;

        public GrainId GrainId { get; } = grainId;

        /// <summary>Completes with the grain's runtime once it has started the pending executions which it recovered.</summary>
        public Task<DurableTaskGrainRuntime> Recovered => _recovered.Task;

        public void OnRecovered(DurableTaskGrainRuntime runtime) => _recovered.TrySetResult(runtime);

        public void Update(TaskId executionId, Action<Execution> update)
        {
            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }

                var execution = _executions.FindLast(candidate => candidate.Id == executionId);
                if (execution is null)
                {
                    execution = new(executionId);
                    _executions.Add(execution);
                }

                update(execution);
            }
        }

        /// <summary>Builds the result from what the runtime reported, and ignores later reports.</summary>
        public DurableTaskReplayResult Complete()
        {
            lock (_lock)
            {
                _completed = true;
                return new(GrainId, [.. _executions.Select(static execution => execution.ToReplay())]);
            }
        }
    }

    /// <summary>What the runtime reported about the attempt of one execution.</summary>
    internal sealed class Execution(TaskId id)
    {
        private readonly List<TaskId> _reached = [];
        private HashSet<TaskId> _recorded = [];
        private TaskId? _newOperation;
        private string? _newOperationDescription;
        private bool _ended;
        private DurableTaskResponse? _response;
        private DurableTaskAttemptCanceledException? _cancellation;

        public TaskId Id { get; } = id;

        public List<IReadOnlyCollection<TaskId>> Waits { get; } = [];

        public Dictionary<string, int> Versions { get; } = new(StringComparer.Ordinal);

        public void Start(IReadOnlyCollection<TaskId> recorded) => _recorded = [.. recorded];

        public void Reach(TaskId operationId)
        {
            if (!_reached.Contains(operationId))
            {
                _reached.Add(operationId);
            }
        }

        public void Stop(TaskId operationId, string description)
        {
            _newOperation ??= operationId;
            _newOperationDescription ??= description;
        }

        public void End(DurableTaskResponse response, DurableTaskAttemptCanceledException? cancellation)
        {
            _ended = true;
            _response = response;
            _cancellation = cancellation;
        }

        public DurableTaskExecutionReplay ToReplay()
        {
            DurableTaskReplayOutcome outcome;
            DurableTaskResponse? response = null;
            DurableTaskAttemptCanceledException? stall = null;
            if (_newOperation is not null)
            {
                outcome = DurableTaskReplayOutcome.ReachedNewOperation;
            }
            else if (_ended && _cancellation is { } cancellation)
            {
                // The replay host cancels only attempts which reach new operations, so another cancellation stalled the
                // execution, such as one for an unsupported version or a divergence.
                outcome = DurableTaskReplayOutcome.Stalled;
                stall = cancellation;
            }
            else if (_ended && _response is { IsCompleted: true } completed)
            {
                response = completed;
                outcome = completed.Exception is null or OperationCanceledException
                    ? DurableTaskReplayOutcome.Completed
                    : DurableTaskReplayOutcome.Faulted;
            }
            else
            {
                outcome = DurableTaskReplayOutcome.Waiting;
            }

            var reached = new HashSet<TaskId>(_reached);
            return new(
                Id,
                outcome,
                response,
                stall,
                [.. _reached],
                [.. _recorded.Where(id => !reached.Contains(id)).OrderBy(static id => id.ToString(), StringComparer.Ordinal)],
                _newOperation,
                _newOperationDescription,
                [.. Waits.SelectMany(static wait => wait).Distinct()],
                new Dictionary<string, int>(Versions, StringComparer.Ordinal));
        }
    }
}
