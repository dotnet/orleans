#nullable enable
using System;
using System.Distributed.DurableTasks;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.DurableTasks;

internal sealed partial class DurableTaskGrainRuntime
{
    private class TaskHandle(TaskId taskId, DurableTaskGrainRuntime runtime) : IScheduledTaskHandle
    {
        private readonly TaskCompletionSource<DurableTaskResponse> _responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DurableTaskResponse> ResponseTask => _responseTcs.Task;

        public TaskId TaskId { get; } = taskId;

        // If this is false, the task was rehydrated from storage but has not started running yet.
        // This will happen if the task is non-serializable, like a local method invocation.
        public bool IsRunning { get; set; }

        public async ValueTask CancelAsync(CancellationToken cancellationToken)
        {
            await runtime.SignalCancellationAsync(TaskId, cancellationToken);
        }

        public async ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
        {
            if (options.PollTimeout > TimeSpan.Zero)
            {
                await ((Task)ResponseTask).WaitAsync(options.PollTimeout, runtime._shared.TimeProvider, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            }

            if (ResponseTask.IsCompleted)
            {
                return await ResponseTask;
            }

            return DurableTaskResponse.Pending;
        }

        public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
        {
            return await ResponseTask.WaitAsync(cancellationToken);
        }

        public bool TrySetResponse(DurableTaskResponse response) => _responseTcs.TrySetResult(response);
    }
}
