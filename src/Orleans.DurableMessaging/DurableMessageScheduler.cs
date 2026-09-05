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
internal sealed class ScheduledMessageState : IDisposable
{
    [Id(0)]
    public required DurableEnvelope Message { get; init; }

    [Id(1)]
    public DateTimeOffset DueTime { get; init; }

    [Id(2)]
    public string? JobId { get; set; }

    public void Dispose() => Message.Dispose();
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
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DurableMessageScheduler(
        [FromKeyedServices("scheduled-messages")] IDurableDictionary<Guid, ScheduledMessageState> messages,
        IJournaledStateManager stateManager,
        ILocalDurableJobManager jobManager,
        IDurableJobHandlerRegistry handlers,
        IDurableOutbox outbox,
        IGrainContext grainContext,
        [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider? timeProvider = null)
    {
        _messages = messages;
        _stateManager = stateManager;
        _jobManager = jobManager;
        _outbox = outbox;
        _grainContext = grainContext;
        _timeProvider = timeProvider ?? TimeProvider.System;
        handlers.Register(this, requiresTurnIsolation: true);
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
            if (_messages.TryGetValue(message.MessageId, out var existing))
            {
                if (!ReferenceEquals(existing.Message.Data, message.Data))
                {
                    message.Dispose();
                }

                await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
                await EnsureJobOrDeactivateUnderGateAsync(message.MessageId).ConfigureAwait(true);
                return;
            }

            _messages[message.MessageId] = new ScheduledMessageState
            {
                Message = message,
                DueTime = dueTime
            };
            await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
            await EnsureJobOrDeactivateUnderGateAsync(message.MessageId).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool CanHandle(string jobName) => string.Equals(jobName, JobName, StringComparison.Ordinal);

    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(
        IJobRunContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await _stateManager.InitializeAsync(cancellationToken).ConfigureAwait(true);
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

                var message = state.Message.Retain();
                try
                {
                    _outbox.Send(message);
                }
                finally
                {
                    message.Dispose();
                }
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DurableJobRunResult.RescheduleAt(_timeProvider.GetUtcNow() + TimeSpan.FromSeconds(1));
        }
    }

    public async Task OnStart(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var scheduleIds = _messages.Keys.ToList();
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

    private async ValueTask EnsureJobOrDeactivateUnderGateAsync(Guid scheduleId)
    {
        try
        {
            await EnsureJobUnderGateAsync(scheduleId, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _grainContext.Deactivate(
                new DeactivationReason(
                    DeactivationReasonCode.ApplicationError,
                    exception,
                    $"Failed to schedule durable delivery for message '{scheduleId}'."),
                CancellationToken.None);
            throw;
        }
    }
}
