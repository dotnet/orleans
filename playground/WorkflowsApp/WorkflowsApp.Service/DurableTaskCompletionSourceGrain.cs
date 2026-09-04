using System.Distributed.DurableTasks;
using Orleans.Journaling;

namespace WorkflowsApp.Service;

public class DurableTaskCompletionSourceGrain<T>([FromKeyedServices("state")] IDurableTaskCompletionSource<T> state) : DurableGrain, IDurableTaskCompletionSourceGrain<T>
{
    public async ValueTask<bool> TrySetResult(T value)
    {
        if (state.TrySetResult(value))
        {
            await WriteStateAsync();
            return true;
        }

        return false;
    }

    public async ValueTask<bool> TrySetException(Exception exception)
    {
        if (state.TrySetException(exception))
        {
            await WriteStateAsync();
            return true;
        }

        return false;
    }

    public async ValueTask<bool> TrySetCanceled()
    {
        if (state.TrySetCanceled())
        {
            await WriteStateAsync();
            return true;
        }

        return false;
    }

    public async DurableTask<DurableTaskCompletionSourceState<T>> GetCompletionState()
    {
        var context = DurableExecutionContext.CurrentContext
            ?? throw new InvalidOperationException("A durable execution context is required.");
        using var cancellation = new CancellationTokenSource();
        using var registration = context.RegisterCancellationCallback(
            static (source, _) => source.CancelAsync(),
            cancellation);
        using var deactivationRegistration = context.RegisterDeactivationCallback(
            static (source, _) => source.CancelAsync(),
            cancellation);
        try
        {
            await ((Task)state.Task).WaitAsync(cancellation.Token);
        }
        catch when (state.Task.IsCompleted)
        {
        }

        return state.State;
    }

    public async DurableTask<T> GetResult()
    {
        var context = DurableExecutionContext.CurrentContext
            ?? throw new InvalidOperationException("A durable execution context is required.");
        using var cancellation = new CancellationTokenSource();
        using var registration = context.RegisterCancellationCallback(
            static (source, _) => source.CancelAsync(),
            cancellation);
        using var deactivationRegistration = context.RegisterDeactivationCallback(
            static (source, _) => source.CancelAsync(),
            cancellation);
        return await state.Task.WaitAsync(cancellation.Token);
    }
    public ValueTask<DurableTaskCompletionSourceState<T>> GetState() => new(state.State);
}
