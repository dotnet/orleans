namespace Orleans.Journaling;

/// <summary>
/// The exception which an access to journaled state throws when an open transaction of the state's manager holds the state
/// and the access, from outside the transaction, would change it or record what it read.
/// </summary>
/// <remarks>
/// Writes from code outside the transaction, such as a reentrant or always-interleaving request, would interleave with the
/// transaction's changes and could be lost or kept by its abort, so they throw. A durable task body's recorded reads throw
/// too, because a record of an uncommitted value would outlive the transaction's abort; the durable task runtime turns the
/// conflict into a stall of the body's attempt, and a new attempt reruns the body once the transaction ends. The state
/// keeps its content: the access doesn't run.
/// </remarks>
internal sealed class JournaledStateConflictException : InvalidOperationException
{
    private readonly IStateMachine _state;

    internal JournaledStateConflictException(JournaledStateManager manager, IStateMachine state, string stateName)
        : base($"Journaled state '{stateName}' is held by an open transaction: only the transaction's code can change it, or read it for a record, until the transaction ends.")
    {
        Manager = manager;
        _state = state;
        StateName = stateName;
    }

    /// <summary>Gets the manager whose open transaction holds the state.</summary>
    public JournaledStateManager Manager { get; }

    /// <summary>Gets the name of the held state.</summary>
    public string StateName { get; }

    /// <summary>Completes once no open transaction of <see cref="Manager"/> holds the state.</summary>
    /// <remarks>Called on the activation's scheduler, from code outside the transaction.</remarks>
    public ValueTask WaitForReleaseAsync(CancellationToken cancellationToken) => Manager.WaitUntilReleasedAsync(_state, cancellationToken);
}
