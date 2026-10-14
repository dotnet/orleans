using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

internal sealed partial class DurableTaskGrainRuntime
{
    private class TaskHandle(
        TaskId taskId,
        DurableTaskGrainRuntime runtime,
        GrainId remoteTarget = default) : IScheduledTaskHandle
    {
        private readonly TaskCompletionSource<DurableTaskResponse> _responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GrainId _remoteTarget = remoteTarget;
        private int _isRunning;

        public Task<DurableTaskResponse> ResponseTask => _responseTcs.Task;

        public TaskId TaskId { get; } = taskId;
        public GrainId RemoteTarget => _remoteTarget;

        // If this is false, the task was rehydrated from storage but has not started running yet.
        // This will happen if the task is non-serializable, like a local method invocation.
        public bool IsRunning
        {
            get => Volatile.Read(ref _isRunning) != 0;
            set => Volatile.Write(ref _isRunning, value ? 1 : 0);
        }

        public ValueTask CancelAsync(CancellationToken cancellationToken) => CancelAsync(reason: null, cancellationToken);

        public async ValueTask CancelAsync(string? reason, CancellationToken cancellationToken)
        {
            ValueTask cancellation;
            if (RemoteTarget.IsDefault)
            {
                cancellation = runtime.CancelScheduledTaskAsync(TaskId, reason, CancellationToken.None);
            }
            else
            {
                cancellation = runtime.CancelRemoteAsync(TaskId, RemoteTarget, reason, CancellationToken.None);
            }

            await cancellation.AsTask().WaitAsync(cancellationToken);
        }

        public async ValueTask AbortAsync(string reason, CancellationToken cancellationToken) =>
            await runtime.AbortTaskAsync(TaskId, reason, CancellationToken.None).AsTask().WaitAsync(cancellationToken);

        public async ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.PollTimeout > TimeSpan.Zero)
            {
                try
                {
                    await ((Task)ResponseTask)
                        .WaitAsync(options.PollTimeout, runtime._shared.TimeProvider, cancellationToken)
                        .ConfigureAwait(true);
                }
                catch (TimeoutException)
                {
                    return DurableTaskResponse.Pending;
                }
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
        public bool TrySetException(Exception exception) => _responseTcs.TrySetException(exception);
    }
}
