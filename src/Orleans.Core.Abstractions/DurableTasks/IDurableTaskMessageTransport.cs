using System.Distributed.DurableTasks;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

internal interface IDurableTaskMessageTransport
{
    void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request);

    void SendCompletion(GrainId sender, GrainId target, TaskId taskId, DurableTaskResponse response);

    void SendCancellation(GrainId sender, GrainId target, TaskId taskId);

    void SendCancellationAcknowledgement(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        DurableTaskResponse response);

    ValueTask ScheduleResumeAsync(GrainId target, TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken);

    ValueTask CommitAsync(CancellationToken cancellationToken);
}

internal interface IDurableTaskMessageTransaction
{
    bool TryEnlist(Action commit, Action rollback);
    void EnlistNextCommit(Action commit, Action rollback);
    ValueTask CommitAsync(
        Func<ValueTask> prepare,
        Action commit,
        Action rollback,
        CancellationToken cancellationToken);
}
