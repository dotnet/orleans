#nullable enable
using System;
using System.Distributed.DurableTasks;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.DurableTasks;

internal sealed partial class DurableTaskGrainRuntime
{
    private class TaskHandle(
        TaskId taskId,
        DurableTaskGrainRuntime runtime,
        GrainId remoteTarget = default) : IScheduledTaskHandle
    {
        private readonly TaskCompletionSource<DurableTaskResponse> _responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DurableTaskResponse> ResponseTask => _responseTcs.Task;

        public TaskId TaskId { get; } = taskId;

        public GrainId RemoteTarget { get; } = remoteTarget;

        // If this is false, the task was rehydrated from storage but has not started running yet.
        // This will happen if the task is non-serializable, like a local method invocation.
        public bool IsRunning { get; set; }

        public async ValueTask CancelAsync(CancellationToken cancellationToken)
        {
            if (RemoteTarget.IsDefault)
            {
                await runtime.CancelScheduledTaskAsync(TaskId, cancellationToken);
            }
            else
            {
                await runtime.CancelRemoteAsync(TaskId, RemoteTarget, cancellationToken);
                _ = await ResponseTask.WaitAsync(cancellationToken);
            }
        }

        public async ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
        {
            if (options.PollTimeout > TimeSpan.Zero)
            {
                using var timeout = new CancellationTokenSource(options.PollTimeout, runtime._shared.TimeProvider);
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    await ((Task)ResponseTask).WaitAsync(linkedCancellation.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }

                cancellationToken.ThrowIfCancellationRequested();
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
