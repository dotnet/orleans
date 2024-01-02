#nullable enable
using System;
using System.Diagnostics;
using System.Distributed.DurableTasks;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableTasks;

/*
internal sealed class ClientDurableTaskProxy(IGrainFactory grainFactory) : IDurableTaskProxy
{
    public IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId, ISchedulableTask taskDefinition)
    {
        if (taskDefinition is IDurableTaskRequest grainRequest)
        {
            var context = grainRequest.Context;
            Debug.Assert(context is not null);
            var grain = grainFactory.GetGrain<IDurableTaskServer>(context.TargetId);
            return new ScheduledTaskHandle(taskId, grain, lastResponse: null);
        }

        // Schedule the request directly on the target grain.
        var targetGrain = grainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        var response = await targetGrain.ScheduleAsync(this);
        return new GrainScheduledTaskHandle(taskId, targetGrain, lastResponse: response);
    }

    public ValueTask<IScheduledTaskHandle> ScheduleAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

private sealed class ScheduledTaskHandle(TaskId taskId, IDurableTaskServer grain, DurableTaskResponse? lastResponse) : IScheduledTaskHandle
{
    public TaskId TaskId { get; } = taskId;
    public DurableTaskResponse? LastResponse { get; private set; } = lastResponse;

    public async ValueTask CancelAsync(CancellationToken cancellationToken)
    {
        // TODO: Add resilience via Polly
        await grain.CancelAsync(TaskId).AsTask().WaitAsync(cancellationToken);
    }

    public async ValueTask<DurableTaskResponse> PollAsync(CancellationToken cancellationToken)
    {
        if (LastResponse is { IsCompleted: true } response)
        {
            return response;
        }

        // TODO: Add resilience via Polly
        return LastResponse = await grain.SubscribeOrPollAsync(TaskId, client: null).AsTask().WaitAsync(cancellationToken);
    }

    public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
    {
        if (LastResponse is { IsCompleted: true } response)
        {
            return response;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // TODO: Add resilience via Polly

            response = LastResponse = await grain.SubscribeOrPollAsync(TaskId, client: null).AsTask().WaitAsync(cancellationToken);
            if (response.IsCompleted)
            {
                return response;
            }

            // TODO: Add exponential backoff via Polly/etc
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }
}
}
*/
