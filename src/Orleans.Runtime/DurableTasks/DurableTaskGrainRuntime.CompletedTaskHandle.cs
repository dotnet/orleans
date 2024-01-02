#nullable enable
using System.Distributed.DurableTasks;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.DurableTasks;

internal sealed partial class DurableTaskGrainRuntime
{
    private sealed class CompletedTaskHandle(TaskId taskId, DurableTaskResponse response) : IScheduledTaskHandle
    {
        public TaskId TaskId => taskId;

        public DurableTaskResponse Response => response;

        public ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken) => new(response);

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) => new(response);
    }
}
