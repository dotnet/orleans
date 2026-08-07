using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

namespace Orleans.DurableMessaging;

[GenerateSerializer]
internal sealed class ScheduledMessageState
{
    [Id(0)]
    public required DurableEnvelope Message { get; init; }

    [Id(1)]
    public DateTimeOffset DueTime { get; init; }

    [Id(2)]
    public string? JobId { get; set; }
}

internal sealed class DurableMessageScheduler :
    IDurableMessageScheduler,
    IDurableJobFeatureHandler,
    ILifecycleObserver
{
    internal const string JobName = "orleans.messaging.scheduled-delivery";
    private const string ScheduleIdMetadataKey = "schedule-id";

    private readonly IDurableDictionary<Guid, ScheduledMessageState> _messages;
    private readonly IJournaledStateManager _stateManager;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly IDurableOutbox _outbox;
    private readonly IGrainContext _grainContext;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DurableMessageScheduler(
        [FromKeyedServices("scheduled-messages")] IDurableDictionary<Guid, ScheduledMessageState> messages,
        IJournaledStateManager stateManager,
        ILocalDurableJobManager jobManager,
        IDurableJobHandlerRegistry handlers,
        IDurableOutbox outbox,
        IGrainContext grainContext)
    {
        _messages = messages;
        _stateManager = stateManager;
        _jobManager = jobManager;
        _outbox = outbox;
        _grainContext = grainContext;
        handlers.Register(JobName, this);
        grainContext.ObservableLifecycle.Subscribe(
            RuntimeTypeNameFormatter.Format(GetType()),
            GrainLifecycleStage.Activate,
            this);
    }

    public async ValueTask ScheduleAsync(
        DurableEnvelope message,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_messages.ContainsKey(message.MessageId))
            {
                await EnsureJobUnderGateAsync(message.MessageId, cancellationToken).ConfigureAwait(true);
                return;
            }

            _messages[message.MessageId] = new ScheduledMessageState
            {
                Message = message,
                DueTime = dueTime
            };
            await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
            await EnsureJobUnderGateAsync(message.MessageId, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(
        IJobRunContext context,
        CancellationToken cancellationToken)
    {
        if (context.Job.Metadata is null
            || !context.Job.Metadata.TryGetValue(ScheduleIdMetadataKey, out var value)
            || !Guid.TryParse(value, out var scheduleId))
        {
            return DurableJobRunResult.Completed;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!_messages.TryGetValue(scheduleId, out var state))
            {
                return DurableJobRunResult.Completed;
            }

            if (state.JobId is null)
            {
                state.JobId = context.Job.Id;
                _messages[scheduleId] = state;
                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
            }
            else if (!string.Equals(state.JobId, context.Job.Id, StringComparison.Ordinal))
            {
                return DurableJobRunResult.Completed;
            }

            _outbox.Send(state.Message);
            _messages.Remove(scheduleId);
            try
            {
                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
            }
            catch
            {
                await _stateManager.RevertPendingChangesAsync(CancellationToken.None).ConfigureAwait(true);
                throw;
            }
            return DurableJobRunResult.Completed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OnStart(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var scheduleIds = _messages.Keys.ToList();
            if (scheduleIds.Count > 0)
            {
                foreach (var scheduleId in scheduleIds)
                {
                    var state = _messages[scheduleId];
                    state.JobId = null;
                    _messages[scheduleId] = state;
                }

                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
            }

            foreach (var scheduleId in scheduleIds)
            {
                await EnsureJobUnderGateAsync(scheduleId, cancellationToken).ConfigureAwait(true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task OnStop(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private async ValueTask EnsureJobUnderGateAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        if (!_messages.TryGetValue(scheduleId, out var state) || state.JobId is not null)
        {
            return;
        }

        var job = await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = _grainContext.GrainId,
                JobName = JobName,
                DueTime = state.DueTime,
                Metadata = new Dictionary<string, string>
                {
                    [ScheduleIdMetadataKey] = scheduleId.ToString()
                }
            },
            cancellationToken).ConfigureAwait(true);

        if (_messages.TryGetValue(scheduleId, out state) && state.JobId is null)
        {
            state.JobId = job.Id;
            _messages[scheduleId] = state;
            await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
        }
    }
}
