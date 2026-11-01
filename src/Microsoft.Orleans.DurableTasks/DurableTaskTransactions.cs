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
    /// <returns>The body's result, once a journal write holds the transaction's commit.</returns>
    /// <remarks>
    /// <para>
    /// The transaction is recorded as an effect, in the same journal write as its changes: a replay returns the recorded
    /// result, or throws the recorded exception, without running the body. The call returns once that write has
    /// completed. A write which fails fails the call and the grain's journal, so the next activation runs the body again.
    /// </para>
    /// <para>
    /// The body runs exclusively on the activation, as <c>JournaledStateManager.RunTransactionAsync</c> describes. Its
    /// accesses to journaled state aren't recorded, an effect inside it runs directly, and a durable operation inside it
    /// throws, since a replay returns the transaction's record without running the body.
    /// </para>
    /// <para>
    /// A request to cancel the task doesn't change how the transaction commits: its record commits with its changes. A
    /// transaction in an attempt which the runtime discarded, for example because its activation stopped, aborts and
    /// records nothing, since the next attempt runs the body again. Once the task can't replay, because the runtime aborted
    /// or deleted it or its body completed, a transaction, such as one in the abort's cleanup, commits its changes durably
    /// without a record, as the attempt's other writes then apply.
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
