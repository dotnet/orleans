using System.Diagnostics;
using DurableJobsJournaling.Abstractions;
using Orleans.DurableJobs;

namespace DurableJobsJournaling.Silo;

[CollectionAgeLimit(Minutes = 1.01)]
public sealed class WorkflowCoordinatorGrain(ILocalDurableJobManager jobManager)
    : Grain, IWorkflowCoordinatorGrain, IDurableJobHandler
{
    private const string WorkflowJobName = "workflow";

    private WorkflowState? _state;
    private List<WorkflowStageRecord> stages = [];
    TaskCompletionSource<WorkflowResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Stopwatch? _workflowStopwatch;

    public async Task StartAsync(WorkflowStartRequest request)
    {
        if (_state is { Status: WorkflowStatus.Running })
        {
            return;
        }

        stages.Clear();
        _workflowStopwatch = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;
        var settings = request.Settings.Normalize();
        _state = new WorkflowState(
            request.WorkflowId,
            WorkflowStatus.Running,
            WorkflowStage.Accept,
            request.CreatedAt,
            now,
            CompletedAt: null,
            RetryCount: 0,
            FailureCount: 0,
            Error: null,
            settings);

        //var writeTask = WriteStateAsync();
        var scheduleTask = jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = this.GetGrainId(),
                JobName = WorkflowJobName,
                DueTime = now
            },
            CancellationToken.None);
        await scheduleTask;
    }

    public async Task<WorkflowResult> AwaitCompletionAsync() => await _completion.Task;

    public Task<WorkflowSnapshot> GetSnapshotAsync()
    {
        var completionState = _completion;
        return Task.FromResult(new WorkflowSnapshot(
            _state,
            stages.ToArray(),
            [],
            completionState.Task.IsCompleted));
    }

    public Task CancelAsync()
    {
        var now = DateTimeOffset.UtcNow;
        _state = (_state ?? CreateDefaultState(now)) with
        {
            Status = WorkflowStatus.Canceled,
            CompletedAt = now,
            UpdatedAt = now,
            Error = "Canceled"
        };

        _completion.TrySetResult(CreateResult(now, "Canceled"));
        return Task.CompletedTask;
    }

    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(10);
        var currentState = _state ?? CreateDefaultState(DateTimeOffset.UtcNow);
        if (currentState.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Canceled)
        {
            return;
        }

        var transitionTiming = CreateCurrentStageTransitionTiming(context.Job.DueTime, currentState);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentState = _state ?? currentState;
            if (currentState.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Canceled)
            {
                return;
            }

            var stage = currentState.ActiveStage;
            var startedAt = DateTimeOffset.UtcNow;
            var stageStopwatch = Stopwatch.StartNew();
            var attempt = CalculateStageAttempt(stage);
            _state = currentState with
            {
                Status = WorkflowStatus.Running,
                ActiveStage = stage,
                UpdatedAt = startedAt,
                RetryCount = CalculateRetryCount(),
                FailureCount = CalculateFailureCount(),
                Error = null
            };

            stageStopwatch.Stop();
            var completedAt = startedAt.Add(stageStopwatch.Elapsed);
            stages.Add(CreateStageRecord(
                stage,
                attempt,
                context.Job.Id,
                transitionTiming,
                startedAt,
                completedAt,
                stageStopwatch.Elapsed,
                succeeded: true,
                error: null));

            if (IsTerminalStage(stage))
            {
                _state = _state with
                {
                    Status = WorkflowStatus.Completed,
                    CompletedAt = completedAt,
                    UpdatedAt = completedAt,
                    RetryCount = CalculateRetryCount(),
                    FailureCount = CalculateFailureCount(),
                    Error = null
                };

                _completion.TrySetResult(CreateResult(completedAt, error: null));
            }
            else
            {
                var nextStage = GetNextStage(stage);
                _state = _state with
                {
                    ActiveStage = nextStage,
                    UpdatedAt = completedAt,
                    RetryCount = CalculateRetryCount(),
                    FailureCount = CalculateFailureCount(),
                    Error = null
                };
            }

            var stateWriteStartedAt = DateTimeOffset.UtcNow;
            var stateWriteStopwatch = Stopwatch.StartNew();
            stateWriteStopwatch.Stop();
            var stateWriteCompletedAt = stateWriteStartedAt.Add(stateWriteStopwatch.Elapsed);
            transitionTiming = StageTransitionTiming.ForNextStage(completedAt, stateWriteStartedAt, stateWriteCompletedAt);
        }
    }

    private WorkflowResult CreateResult(DateTimeOffset completedAt, string? error)
    {
        var currentState = _state ?? CreateDefaultState(completedAt);
        var status = error is null ? currentState.Status : WorkflowStatus.Failed;
        var totalLatencyMilliseconds = GetTotalLatencyMilliseconds(currentState, completedAt);
        return new WorkflowResult(
            currentState.WorkflowId,
            status,
            currentState.CreatedAt,
            completedAt,
            totalLatencyMilliseconds,
            stages.ToArray(),
            [],
            CalculateRetryCount(),
            CalculateFailureCount(),
            error);
    }

    private double GetTotalLatencyMilliseconds(WorkflowState currentState, DateTimeOffset completedAt)
    {
        if (_workflowStopwatch is { } workflowStopwatch)
        {
            workflowStopwatch.Stop();
            return workflowStopwatch.Elapsed.TotalMilliseconds;
        }

        return (completedAt - currentState.CreatedAt).TotalMilliseconds;
    }

    private WorkflowState CreateDefaultState(DateTimeOffset now)
        => new(this.GetPrimaryKey(), WorkflowStatus.NotStarted, WorkflowStage.Accept, now, now, null, 0, 0, null, LoadSettings.Default);

    private static WorkflowStage GetNextStage(WorkflowStage stage) => stage switch
    {
        WorkflowStage.Accept => WorkflowStage.ReserveInventory,
        WorkflowStage.ReserveInventory => WorkflowStage.ChargePayment,
        WorkflowStage.ChargePayment => WorkflowStage.PackShipment,
        WorkflowStage.PackShipment => WorkflowStage.Audit,
        WorkflowStage.Audit => WorkflowStage.Complete,
        _ => WorkflowStage.Complete
    };

    private static bool IsTerminalStage(WorkflowStage stage) => stage is WorkflowStage.Accept or WorkflowStage.Audit or WorkflowStage.Complete;

    private WorkflowStageRecord CreateStageRecord(
        WorkflowStage stage,
        int attempt,
        string jobId,
        StageTransitionTiming transitionTiming,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        TimeSpan executionElapsed,
        bool succeeded,
        string? error)
        => new(
            stage,
            attempt,
            jobId,
            transitionTiming.ScheduledAt,
            startedAt,
            completedAt,
            (startedAt - transitionTiming.ScheduledAt).TotalMilliseconds,
            executionElapsed.TotalMilliseconds,
            (startedAt - transitionTiming.PreviousStageCompletedAt).TotalMilliseconds,
            ChildCount: 0,
            succeeded,
            error,
            transitionTiming.PreviousStageCompletedAt,
            transitionTiming.StateWriteStartedAt,
            transitionTiming.StateWriteCompletedAt,
            transitionTiming.ScheduleRequestedAt,
            transitionTiming.ScheduleCompletedAt);

    private StageTransitionTiming CreateCurrentStageTransitionTiming(DateTimeOffset jobDueTime, WorkflowState currentState)
    {
        var previousStage = currentState.ActiveStage is WorkflowStage.Accept
            ? null
            : stages.LastOrDefault(static stage => stage.Succeeded);
        var previousCompletedAt = previousStage?.CompletedAt ?? currentState.CreatedAt;
        var scheduledAt = jobDueTime > previousCompletedAt ? jobDueTime : previousCompletedAt;
        return new StageTransitionTiming(
            scheduledAt,
            previousCompletedAt,
            StateWriteStartedAt: default,
            StateWriteCompletedAt: default,
            ScheduleRequestedAt: default,
            ScheduleCompletedAt: default);
    }

    private int CalculateRetryCount() => stages.Where(static stage => stage.Succeeded).Sum(static stage => Math.Max(0, stage.Attempt - 1));

    private int CalculateFailureCount() => stages.Count(static stage => !stage.Succeeded);

    private int CalculateStageAttempt(WorkflowStage stage) => stages.Count(record => record.Stage == stage && !record.Succeeded) + 1;

    private readonly record struct StageTransitionTiming(
        DateTimeOffset ScheduledAt,
        DateTimeOffset PreviousStageCompletedAt,
        DateTimeOffset StateWriteStartedAt,
        DateTimeOffset StateWriteCompletedAt,
        DateTimeOffset ScheduleRequestedAt,
        DateTimeOffset ScheduleCompletedAt)
    {
        public static StageTransitionTiming ForNextStage(
            DateTimeOffset previousStageCompletedAt,
            DateTimeOffset stateWriteStartedAt,
            DateTimeOffset stateWriteCompletedAt)
            => new(
                ScheduledAt: stateWriteCompletedAt,
                previousStageCompletedAt,
                stateWriteStartedAt,
                stateWriteCompletedAt,
                ScheduleRequestedAt: stateWriteCompletedAt,
                ScheduleCompletedAt: stateWriteCompletedAt);
    }
}
