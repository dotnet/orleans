namespace Orleans.Runtime.DurableTasks;

internal interface IDurableTaskTurnIsolation
{
    ValueTask<IDurableTaskTurnIsolationLease> EnterAsync(CancellationToken cancellationToken);
}

internal interface IDurableTaskTurnIsolationLease : IDisposable
{
    void Activate();
}

internal interface IDurableTaskContinuationScheduler
{
    Action WrapContinuation(Action continuation);
}
