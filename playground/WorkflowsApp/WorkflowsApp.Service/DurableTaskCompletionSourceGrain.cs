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
        // Wait for the result to complete, without throwing.
        var nonGenericTask = (Task)state.Task;
        await nonGenericTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);

        return state.State;
    }

    public async DurableTask<T> GetResult() => await state.Task;
    public ValueTask<DurableTaskCompletionSourceState<T>> GetState() => new(state.State);
}
