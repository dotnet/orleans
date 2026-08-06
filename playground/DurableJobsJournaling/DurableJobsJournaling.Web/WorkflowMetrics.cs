using System.Diagnostics;
using System.Diagnostics.Metrics;
using DurableJobsJournaling.Abstractions;

namespace DurableJobsJournaling.Web;

public sealed class WorkflowMetrics
{
    private static readonly Meter Meter = new("Orleans.Playground.DurableJobsJournaling");
    private readonly Counter<long> _started = Meter.CreateCounter<long>("playground.workflow.started");
    private readonly Counter<long> _completed = Meter.CreateCounter<long>("playground.workflow.completed");
    private readonly Counter<long> _failed = Meter.CreateCounter<long>("playground.workflow.failed");
    private readonly Counter<long> _retries = Meter.CreateCounter<long>("playground.workflow.retries");
    private readonly Histogram<double> _endToEndLatency = Meter.CreateHistogram<double>("playground.workflow.duration", unit: "ms");
    private readonly Histogram<double> _scheduleToStartLatency = Meter.CreateHistogram<double>("playground.stage.schedule_to_start", unit: "ms");
    private readonly Histogram<double> _stageHandoffLatency = Meter.CreateHistogram<double>("playground.stage.handoff", unit: "ms");
    private readonly Histogram<double> _stageStateWriteLatency = Meter.CreateHistogram<double>("playground.stage.state_write", unit: "ms");
    private readonly Histogram<double> _stageScheduleCallLatency = Meter.CreateHistogram<double>("playground.stage.schedule_call", unit: "ms");
    private readonly Histogram<double> _stageDueWaitLatency = Meter.CreateHistogram<double>("playground.stage.due_wait", unit: "ms");
    private readonly Histogram<double> _stageQueueLatency = Meter.CreateHistogram<double>("playground.stage.queue_delay", unit: "ms");
    private readonly Histogram<double> _stageExecutionLatency = Meter.CreateHistogram<double>("playground.stage.execution", unit: "ms");
    private readonly Histogram<double> _stageTotalLatency = Meter.CreateHistogram<double>("playground.stage.total", unit: "ms");
    private readonly Histogram<double> _stageWorkflowElapsedLatency = Meter.CreateHistogram<double>("playground.stage.workflow_elapsed", unit: "ms");
    private readonly Histogram<double> _stageUnaccountedLatency = Meter.CreateHistogram<double>("playground.stage.unaccounted", unit: "ms");
    private readonly Histogram<double> _rescheduleGapLatency = Meter.CreateHistogram<double>("playground.stage.reschedule_gap", unit: "ms");
    private readonly object _lock = new();
    private readonly Queue<EventSample> _events = [];
    private readonly Queue<double> _endToEndSamples = [];
    private readonly Queue<double> _scheduleToStartSamples = [];
    private readonly Queue<double> _stageExecutionSamples = [];
    private readonly Queue<double> _rescheduleGapSamples = [];
    private readonly Queue<RecentWorkflowSummary> _recent = [];
    private readonly Dictionary<WorkflowStage, StageAccumulator> _stageAccumulators = [];
    private readonly Stopwatch _eventWindowClock = Stopwatch.StartNew();
    private ChaosResult? _lastChaos;

    public void Reset()
    {
        lock (_lock)
        {
            _events.Clear();
            _endToEndSamples.Clear();
            _scheduleToStartSamples.Clear();
            _stageExecutionSamples.Clear();
            _rescheduleGapSamples.Clear();
            _recent.Clear();
            _stageAccumulators.Clear();
            _eventWindowClock.Restart();
            _lastChaos = null;
        }
    }

    public void RecordStarted()
    {
        _started.Add(1);
        AddEvent(EventKind.Started);
    }

    public void RecordCompleted(WorkflowResult result)
    {
        _completed.Add(1);
        _retries.Add(result.RetryCount);
        _endToEndLatency.Record(result.TotalLatencyMilliseconds);

        lock (_lock)
        {
            AddSample(_endToEndSamples, result.TotalLatencyMilliseconds);
            AddRecent(new RecentWorkflowSummary(
                result.WorkflowId,
                result.Status,
                result.CompletedAt,
                result.TotalLatencyMilliseconds,
                result.RetryCount,
                result.FailureCount,
                result.Error));

            foreach (var stage in result.Stages)
            {
                RecordStageLocked(result.CreatedAt, stage);
            }
        }

        AddEvent(EventKind.Completed);
        for (var i = 0; i < result.RetryCount; i++)
        {
            AddEvent(EventKind.Retry);
        }
    }

    public void RecordFailed(WorkflowResult result)
    {
        _failed.Add(1);
        _retries.Add(result.RetryCount);
        lock (_lock)
        {
            AddRecent(new RecentWorkflowSummary(
                result.WorkflowId,
                result.Status,
                result.CompletedAt,
                result.TotalLatencyMilliseconds,
                result.RetryCount,
                result.FailureCount,
                result.Error));

            foreach (var stage in result.Stages)
            {
                RecordStageLocked(result.CreatedAt, stage);
            }
        }

        AddEvent(EventKind.Failed);
    }

    public void RecordFailed(Guid workflowId, Exception exception)
    {
        _failed.Add(1);
        lock (_lock)
        {
            AddRecent(new RecentWorkflowSummary(
                workflowId,
                WorkflowStatus.Failed,
                DateTimeOffset.UtcNow,
                0,
                0,
                1,
                exception.Message));
        }

        AddEvent(EventKind.Failed);
    }

    public void RecordChaos(ChaosResult result)
    {
        lock (_lock)
        {
            _lastChaos = result;
        }
    }

    public RecentWorkflowSummary[] GetRecentWorkflows()
    {
        lock (_lock)
        {
            return _recent.Reverse().ToArray();
        }
    }

    public MetricSnapshot GetSnapshot(LoadDriverState load)
    {
        lock (_lock)
        {
            PruneEvents(_eventWindowClock.Elapsed);
            var now = DateTimeOffset.UtcNow;
            return new MetricSnapshot(
                now,
                load,
                CalculateRatesLocked(),
                Summarize(_endToEndSamples),
                Summarize(_scheduleToStartSamples),
                Summarize(_stageExecutionSamples),
                Summarize(_rescheduleGapSamples),
                _stageAccumulators
                    .OrderBy(static item => item.Key)
                    .Select(static item => item.Value.ToMetric(item.Key))
                    .ToArray(),
                _recent.Reverse().ToArray(),
                _lastChaos);
        }
    }

    private void RecordStageLocked(DateTimeOffset workflowCreatedAt, WorkflowStageRecord stage)
    {
        var phases = StagePhaseMeasurements.Create(workflowCreatedAt, stage);
        var tags = new KeyValuePair<string, object?>[] { new("stage", stage.Stage.ToString()) };
        _scheduleToStartLatency.Record(stage.ScheduleToStartMilliseconds, tags);
        _stageHandoffLatency.Record(phases.HandoffMilliseconds, tags);
        _stageStateWriteLatency.Record(phases.StateWriteMilliseconds, tags);
        _stageScheduleCallLatency.Record(phases.ScheduleCallMilliseconds, tags);
        _stageDueWaitLatency.Record(phases.DueWaitMilliseconds, tags);
        _stageQueueLatency.Record(phases.QueueMilliseconds, tags);
        _stageExecutionLatency.Record(phases.ExecutionMilliseconds, tags);
        _stageTotalLatency.Record(phases.StageTotalMilliseconds, tags);
        _stageWorkflowElapsedLatency.Record(phases.WorkflowElapsedMilliseconds, tags);
        _stageUnaccountedLatency.Record(phases.UnaccountedMilliseconds, tags);
        _rescheduleGapLatency.Record(stage.RescheduleGapMilliseconds, tags);
        AddSample(_scheduleToStartSamples, stage.ScheduleToStartMilliseconds);
        AddSample(_stageExecutionSamples, stage.ExecutionMilliseconds);
        AddSample(_rescheduleGapSamples, stage.RescheduleGapMilliseconds);

        if (!_stageAccumulators.TryGetValue(stage.Stage, out var accumulator))
        {
            accumulator = new StageAccumulator();
            _stageAccumulators[stage.Stage] = accumulator;
        }

        accumulator.Record(stage, phases);
    }

    private void AddRecent(RecentWorkflowSummary summary)
    {
        _recent.Enqueue(summary);
        while (_recent.Count > 50)
        {
            _recent.Dequeue();
        }
    }

    private static void AddSample(Queue<double> samples, double value)
    {
        samples.Enqueue(value);
        while (samples.Count > 5_000)
        {
            samples.Dequeue();
        }
    }

    private void AddEvent(EventKind kind)
    {
        lock (_lock)
        {
            var elapsed = _eventWindowClock.Elapsed;
            _events.Enqueue(new EventSample(elapsed, kind));
            PruneEvents(elapsed);
        }
    }

    private void PruneEvents(TimeSpan now)
    {
        var cutoff = now - TimeSpan.FromSeconds(10);
        while (_events.TryPeek(out var sample) && sample.Elapsed < cutoff)
        {
            _events.Dequeue();
        }
    }

    private RateSnapshot CalculateRatesLocked()
    {
        var windowSeconds = 10d;
        return new RateSnapshot(
            _events.Count(static sample => sample.Kind is EventKind.Started) / windowSeconds,
            _events.Count(static sample => sample.Kind is EventKind.Completed) / windowSeconds,
            _events.Count(static sample => sample.Kind is EventKind.Failed) / windowSeconds,
            _events.Count(static sample => sample.Kind is EventKind.Retry) / windowSeconds);
    }

    private static LatencySummary Summarize(Queue<double> samples)
    {
        if (samples.Count is 0)
        {
            return new LatencySummary(0, 0, 0, 0, 0, 0);
        }

        var values = samples.Order().ToArray();
        return new LatencySummary(
            values.Length,
            values.Average(),
            Percentile(values, 0.50),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            values[^1]);
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        var index = (int)Math.Ceiling(sortedValues.Length * percentile) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
    }

    private sealed record EventSample(TimeSpan Elapsed, EventKind Kind);

    private enum EventKind
    {
        Started,
        Completed,
        Failed,
        Retry
    }

    private sealed class StageAccumulator
    {
        private long _completed;
        private long _failed;
        private double _latencyTotal;
        private readonly Queue<double> _handoffSamples = [];
        private readonly Queue<double> _stateWriteSamples = [];
        private readonly Queue<double> _scheduleCallSamples = [];
        private readonly Queue<double> _dueWaitSamples = [];
        private readonly Queue<double> _queueSamples = [];
        private readonly Queue<double> _executionSamples = [];
        private readonly Queue<double> _stageTotalSamples = [];
        private readonly Queue<double> _workflowElapsedSamples = [];
        private readonly Queue<double> _unaccountedSamples = [];

        public void Record(WorkflowStageRecord stage, StagePhaseMeasurements phases)
        {
            if (stage.Succeeded)
            {
                _completed++;
            }
            else
            {
                _failed++;
            }

            _latencyTotal += stage.ExecutionMilliseconds;
            AddSample(_handoffSamples, phases.HandoffMilliseconds);
            AddSample(_stateWriteSamples, phases.StateWriteMilliseconds);
            AddSample(_scheduleCallSamples, phases.ScheduleCallMilliseconds);
            AddSample(_dueWaitSamples, phases.DueWaitMilliseconds);
            AddSample(_queueSamples, phases.QueueMilliseconds);
            AddSample(_executionSamples, phases.ExecutionMilliseconds);
            AddSample(_stageTotalSamples, phases.StageTotalMilliseconds);
            AddSample(_workflowElapsedSamples, phases.WorkflowElapsedMilliseconds);
            AddSample(_unaccountedSamples, phases.UnaccountedMilliseconds);
        }

        public WorkflowStageMetric ToMetric(WorkflowStage stage)
        {
            var count = _completed + _failed;
            return new WorkflowStageMetric(
                stage,
                _completed,
                _failed,
                count is 0 ? 0 : _latencyTotal / count,
                Summarize(_handoffSamples),
                Summarize(_stateWriteSamples),
                Summarize(_scheduleCallSamples),
                Summarize(_dueWaitSamples),
                Summarize(_queueSamples),
                Summarize(_executionSamples),
                Summarize(_stageTotalSamples),
                Summarize(_workflowElapsedSamples),
                Summarize(_unaccountedSamples));
        }
    }

    private readonly record struct StagePhaseMeasurements(
        double HandoffMilliseconds,
        double StateWriteMilliseconds,
        double ScheduleCallMilliseconds,
        double DueWaitMilliseconds,
        double QueueMilliseconds,
        double ExecutionMilliseconds,
        double StageTotalMilliseconds,
        double WorkflowElapsedMilliseconds,
        double UnaccountedMilliseconds)
    {
        public static StagePhaseMeasurements Create(DateTimeOffset workflowCreatedAt, WorkflowStageRecord stage)
        {
            var previousStageCompletedAt = UseTimestamp(
                stage.PreviousStageCompletedAt,
                stage.Stage is WorkflowStage.Accept ? workflowCreatedAt : stage.ScheduledAt);
            var stateWriteStartedAt = UseTimestamp(stage.StateWriteStartedAt, previousStageCompletedAt);
            var stateWriteCompletedAt = UseTimestamp(stage.StateWriteCompletedAt, stateWriteStartedAt);
            var scheduleRequestedAt = UseTimestamp(stage.ScheduleRequestedAt, stateWriteCompletedAt);
            var scheduleCompletedAt = UseTimestamp(stage.ScheduleCompletedAt, scheduleRequestedAt);
            var readyAt = Max(stage.ScheduledAt, scheduleCompletedAt);

            var handoffMilliseconds = ElapsedMilliseconds(stateWriteStartedAt, previousStageCompletedAt);
            var stateWriteMilliseconds = ElapsedMilliseconds(stateWriteCompletedAt, stateWriteStartedAt);
            var scheduleCallMilliseconds = ElapsedMilliseconds(scheduleCompletedAt, scheduleRequestedAt);
            var dueWaitMilliseconds = ElapsedMilliseconds(readyAt, scheduleCompletedAt);
            var queueMilliseconds = ElapsedMilliseconds(stage.HandlerStartedAt, readyAt);
            var executionMilliseconds = Math.Max(0, stage.ExecutionMilliseconds);
            var stageTotalMilliseconds = ElapsedMilliseconds(stage.CompletedAt, previousStageCompletedAt);
            var workflowElapsedMilliseconds = ElapsedMilliseconds(stage.CompletedAt, workflowCreatedAt);
            var unaccountedMilliseconds = Math.Abs(stageTotalMilliseconds - (
                handoffMilliseconds
                + stateWriteMilliseconds
                + scheduleCallMilliseconds
                + dueWaitMilliseconds
                + queueMilliseconds
                + executionMilliseconds));

            return new StagePhaseMeasurements(
                handoffMilliseconds,
                stateWriteMilliseconds,
                scheduleCallMilliseconds,
                dueWaitMilliseconds,
                queueMilliseconds,
                executionMilliseconds,
                stageTotalMilliseconds,
                workflowElapsedMilliseconds,
                unaccountedMilliseconds);
        }

        private static DateTimeOffset UseTimestamp(DateTimeOffset timestamp, DateTimeOffset fallback)
            => timestamp == default ? fallback : timestamp;

        private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
            => first >= second ? first : second;

        private static double ElapsedMilliseconds(DateTimeOffset end, DateTimeOffset start)
            => Math.Max(0, (end - start).TotalMilliseconds);
    }
}
