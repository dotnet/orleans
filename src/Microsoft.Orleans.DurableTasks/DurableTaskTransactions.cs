using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Runtime;

namespace Orleans.DurableTasks;

/// <summary>Runs transactions over a grain's journaled state from durable task bodies.</summary>
/// <remarks>Internal until transactions ship; the proposed public form is <c>DurableTask.Transaction(name, body)</c>.</remarks>
internal static class DurableTaskTransactions
{
    /// <summary>
    /// Runs <paramref name="body"/> as a transaction over the calling grain's journaled state: its changes commit together
    /// when it returns, or none of them do when it throws.
    /// </summary>
    /// <param name="name">
    /// The transaction's name within the running durable task, which identifies it as <c>DurableTask.RunInline</c>'s name
    /// identifies an effect. Names don't contain <c>$</c>.
    /// </param>
    /// <param name="body">The transaction's code, which awaits ordinary work, such as grain calls, but no durable operations.</param>
    /// <param name="cancellationToken">The token passed to <paramref name="body"/>.</param>
    /// <returns>The body's result, once the transaction has committed.</returns>
    /// <remarks>
    /// <para>
    /// The transaction is recorded as an effect, in the same journal write as its changes: a replay returns the recorded
    /// result, or throws the recorded exception, without running the body. A run which ends before a write captures the
    /// commit loses both, so the next run runs the body again.
    /// </para>
    /// <para>
    /// The body runs exclusively on the activation, as <c>JournaledStateManager.RunTransactionAsync</c> describes. Its
    /// accesses to journaled state aren't recorded.
    /// </para>
    /// </remarks>
    public static Task<T> RunAsync<T>(string name, Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (DurableTaskContext.TryGetCurrent(out var context) && context is GrainDurableTaskContext grainContext)
        {
            return grainContext.RunTransactionAsync(name, body, cancellationToken);
        }

        throw new InvalidOperationException(
            "A durable transaction runs in a durable task on a grain. Other grain code runs transactions through its journaled state manager.");
    }
}
