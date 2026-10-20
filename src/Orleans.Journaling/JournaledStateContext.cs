using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.Journaling;

/// <summary>
/// The context in which code accesses journaled state, which runs each access against the state or supplies its outcome.
/// </summary>
/// <remarks>
/// <para>
/// A host supplies the context of a manager's states through the manager's <see cref="JournaledStateContextProvider"/>,
/// for example the attempt of a durable task body which records the body's accesses and returns the recorded outcomes on
/// replay. A state which takes part asks for the current context at each public operation and, when there is one, runs the
/// operation through it; otherwise the operation runs against the state directly. Every read and write of
/// <see cref="IDurableValue{T}"/>, the durable collections, <see cref="IDurableTaskCompletionSource{T}"/> and the Messaging
/// outbox takes part. Observations which report the state's changes as they happen, such as a completion source's task or
/// a log's stream, run against the state directly.
/// </para>
/// <para>
/// An operation which runs within another operation of the same context, such as an access which a comparer or an
/// enumerated source makes while a collection operation runs, belongs to the outer operation: a provider supplies no
/// context while its context runs an operation.
/// </para>
/// </remarks>
internal abstract class JournaledStateContext
{
    /// <summary>Gets the context in which the calling code accesses <paramref name="target"/>.</summary>
    /// <param name="target">The accessed state, or <see langword="null"/> for state whose accesses always run directly.</param>
    /// <param name="context">The context, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when an access runs through <paramref name="context"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetCurrent([NotNullWhen(true)] JournaledStateAccessTarget? target, [NotNullWhen(true)] out JournaledStateContext? context)
    {
        context = target?.Manager is { HostAccessDepth: 0 } manager ? manager.CurrentContext : null;
        return context is not null;
    }

    /// <summary>Runs <see cref="Access"/> on this context, for a context which wraps another.</summary>
    internal TResult AccessThrough<TArgument, TState, TResult>(
        in JournaledStateAccess access,
        TArgument argument,
        TState state,
        Func<TState, TResult> operation,
        Action<TState, TResult>? apply)
        => Access(access, argument, state, operation, apply);

    /// <summary>
    /// Reads <paramref name="target"/> through this context, with a read whose result depends on the state's content
    /// alone, such as a count or a snapshot. A read whose result also depends on a key, an index or another value uses
    /// <see cref="Read{TArgument, TState, TResult}"/>, which passes that value.
    /// </summary>
    public TResult ReadWhole<TState, TResult>(
        JournaledStateAccessTarget target,
        JournaledStateOperation operation,
        TState state,
        Func<TState, TResult> read)
        => Access(new(target, JournaledStateAccessKind.Read, operation), default(JournaledStateAccessNoArgument), state, read, apply: null);

    /// <summary>
    /// Reads <paramref name="target"/> through this context, with a read whose result depends on the state and on
    /// <paramref name="argument"/>, such as a key or an index.
    /// </summary>
    public TResult Read<TArgument, TState, TResult>(
        JournaledStateAccessTarget target,
        JournaledStateOperation operation,
        TArgument argument,
        TState state,
        Func<TState, TResult> read)
        => Access(new(target, JournaledStateAccessKind.Read, operation), argument, state, read, apply: null);

    /// <summary>Changes <paramref name="target"/> through this context and returns the change's result.</summary>
    public TResult Write<TState, TResult>(
        JournaledStateAccessTarget target,
        JournaledStateOperation operation,
        TState state,
        Func<TState, TResult> write)
        => Access(new(target, JournaledStateAccessKind.Write, operation), default(JournaledStateAccessNoArgument), state, write, apply: null);

    /// <summary>
    /// Changes <paramref name="target"/> through this context with a change which <paramref name="argument"/> describes,
    /// such as a key and a value, and returns the change's result.
    /// </summary>
    public TResult Write<TArgument, TState, TResult>(
        JournaledStateAccessTarget target,
        JournaledStateOperation operation,
        TArgument argument,
        TState state,
        Func<TState, TResult> write)
        => Access(new(target, JournaledStateAccessKind.Write, operation), argument, state, write, apply: null);

    /// <summary>
    /// Changes <paramref name="target"/> through this context with a change whose result <paramref name="observe"/>
    /// determines from the state before <paramref name="apply"/> changes it, such as the item which a dequeue removes, and
    /// returns the result.
    /// </summary>
    /// <remarks>
    /// A context which records the result does so before the change applies, so a result which it can't record, such as a
    /// value without a codec, fails the change and leaves the state unchanged.
    /// </remarks>
    public TResult Write<TState, TResult>(
        JournaledStateAccessTarget target,
        JournaledStateOperation operation,
        TState state,
        Func<TState, TResult> observe,
        Action<TState, TResult> apply)
        => Access(new(target, JournaledStateAccessKind.Write, operation), default(JournaledStateAccessNoArgument), state, observe, apply);

    /// <summary>Changes <paramref name="target"/> through this context.</summary>
    public void Write<TState>(
        JournaledStateAccessTarget target,
        JournaledStateOperation operation,
        TState state,
        Action<TState> write)
        => Write(target, operation, default(JournaledStateAccessNoArgument), state, write);

    /// <summary>Changes <paramref name="target"/> through this context with a change which <paramref name="argument"/> describes.</summary>
    public void Write<TArgument, TState>(
        JournaledStateAccessTarget target,
        JournaledStateOperation operation,
        TArgument argument,
        TState state,
        Action<TState> write)
        => Access(
            new(target, JournaledStateAccessKind.Write, operation),
            argument,
            (State: state, Write: write),
            static state =>
            {
                state.Write(state.State);
                return default(JournaledStateAccessNoResult);
            },
            apply: null);

    /// <summary>
    /// Runs <paramref name="operation"/> against the state which <paramref name="access"/> describes, or supplies its
    /// outcome without running it: a result, or an exception which this throws.
    /// </summary>
    /// <param name="access">The access.</param>
    /// <param name="argument">
    /// The values besides the state which the access depends on: a read's key or index, or a write's key, index, and
    /// written value. It's <see cref="JournaledStateAccessNoArgument"/> for an access which depends only on the state, or
    /// whose arguments are sequences, which a context doesn't enumerate.
    /// </param>
    /// <param name="state">The state which <paramref name="operation"/> and <paramref name="apply"/> take.</param>
    /// <param name="operation">
    /// Runs the access and returns its result; when <paramref name="apply"/> is supplied, determines the result of the
    /// change which <paramref name="apply"/> makes, without changing the state.
    /// </param>
    /// <param name="apply">Makes the change whose result <paramref name="operation"/> returned, or <see langword="null"/>.</param>
    /// <remarks>
    /// <para>
    /// <typeparamref name="TResult"/> is <see cref="JournaledStateAccessNoResult"/> for a write which returns nothing.
    /// </para>
    /// <para>
    /// While the state's <see cref="JournaledStateAccessTarget.Stamp"/> is unchanged, reads of the same operation with equal
    /// arguments return equal results: the same instance, except for operations whose results are copies
    /// (<see cref="JournaledStateOperations.ReturnsCopy"/>). A context which relies on this compares arguments by value, and
    /// only arguments whose equality is exact, such as strings and integers.
    /// </para>
    /// </remarks>
    protected abstract TResult Access<TArgument, TState, TResult>(
        in JournaledStateAccess access,
        TArgument argument,
        TState state,
        Func<TState, TResult> operation,
        Action<TState, TResult>? apply);
}

/// <summary>Supplies the <see cref="JournaledStateContext"/> of the states of one manager.</summary>
/// <remarks>A host installs one provider per manager, through <see cref="IJournaledStateContextHost.ContextProvider"/>.</remarks>
internal abstract class JournaledStateContextProvider
{
    /// <summary>Gets the context of the calling code, or <see langword="null"/> when its accesses run directly.</summary>
    public abstract JournaledStateContext? Current { get; }
}

/// <summary>A journaled state manager whose states run their accesses through a context which a host supplies.</summary>
/// <remarks>
/// A host which records accesses, such as the durable task runtime, requires its manager to implement this, so its
/// states' accesses can't silently bypass the host's context.
/// </remarks>
internal interface IJournaledStateContextHost
{
    /// <summary>
    /// Gets or sets the provider of the context in which code accesses the manager's states, or <see langword="null"/>
    /// when every access runs against the state directly.
    /// </summary>
    JournaledStateContextProvider? ContextProvider { get; set; }

    /// <summary>
    /// Marks the host's own synchronous work, such as the durable task runtime's sends through the outbox, until
    /// <see cref="ExitHostAccess"/>: the accesses which it makes run against the states directly, so no context records
    /// them or supplies their outcomes.
    /// </summary>
    /// <remarks>
    /// The host enters and exits in the same synchronous run of code on the activation's scheduler, so no other code
    /// runs meanwhile. A host owns state which no context should record, such as its own bookkeeping, by creating it
    /// without an access target instead.
    /// </remarks>
    void EnterHostAccess();

    /// <summary>Ends the host's work which <see cref="EnterHostAccess"/> marked.</summary>
    void ExitHostAccess();
}

/// <summary>Identifies journaled state whose accesses run through a <see cref="JournaledStateContext"/>.</summary>
/// <remarks>
/// <see cref="Id"/> is a stable hash of <see cref="Name"/>, so a record of an access identifies its state across processes
/// and deployments without storing the name. The identifiers of one manager's states are distinct: a manager rejects a
/// state whose identifier another of its states has, so its name needs to change.
/// </remarks>
internal sealed class JournaledStateAccessTarget
{
    private long _stamp;

    /// <exception cref="InvalidOperationException">Another state of <paramref name="manager"/> has the same identifier.</exception>
    public JournaledStateAccessTarget(string name, IJournaledStateManager manager, IReplicableState? replicableState = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Id = ComputeId(name);
        Manager = manager as JournaledStateManager;
        ReplicableState = replicableState;
        Manager?.RegisterAccessTarget(this);
    }

    /// <summary>Creates the target of a replica of a state, which no manager registers and no context records.</summary>
    internal JournaledStateAccessTarget(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Id = ComputeId(name);
    }

    /// <summary>Gets the state's journal name.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the stable hash of <see cref="Name"/>, which is unique among the states of <see cref="Manager"/>, so a record
    /// identifies the state which it accessed.
    /// </summary>
    public int Id { get; }

    /// <summary>Gets the manager whose provider supplies the state's context, or <see langword="null"/>.</summary>
    public JournaledStateManager? Manager { get; }

    /// <summary>
    /// Gets the state's support for positional reads, which a context can record as positions of
    /// <see cref="Manager"/>'s journal instead of values, or <see langword="null"/> when its reads record values.
    /// </summary>
    public IReplicableState? ReplicableState { get; }

    /// <summary>
    /// Gets the state's change stamp, which strictly increases with every change of the content which the state's reads
    /// observe, so it never takes the same value twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two equal stamps of one target therefore bracket no change: reads of the same operation with equal arguments return
    /// the same outcome while the stamp is unchanged. A stamp is scoped to its target and lives in memory only: stamps of
    /// different targets, including targets of the same state in different activations, aren't comparable. The stamp
    /// never decreases because state in memory never rolls back: when a journal write fails, the manager fences and the
    /// activation deactivates instead of restoring earlier content.
    /// </para>
    /// <para>
    /// A state changes the content which its reads observe only in its <c>Apply</c> methods, each of which calls
    /// <see cref="OnChanged"/>. Its own operations, whether or not they run through a context, the journal entries and
    /// snapshots which recovery replays, resets, and storage acknowledgements which change what reads observe, such as a
    /// log's committed sequence, all pass through them. In-place changes to a value which a read returned aren't changes
    /// of the state.
    /// </para>
    /// </remarks>
    public long Stamp => _stamp;

    /// <summary>
    /// Records a change of the content which the state's reads observe. The state calls this from each of its
    /// <c>Apply</c> methods, which make every such change, and from nowhere else.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnChanged() => _stamp++;

    // FNV-1a over the name's UTF-16 code units, which, unlike string.GetHashCode, is the same in every process.
    internal static int ComputeId(string name)
    {
        var hash = 2166136261u;
        foreach (var character in name)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return unchecked((int)hash);
    }
}

/// <summary>Describes one access to journaled state.</summary>
internal readonly struct JournaledStateAccess(JournaledStateAccessTarget target, JournaledStateAccessKind kind, JournaledStateOperation operation)
{
    /// <summary>Gets the accessed state.</summary>
    public JournaledStateAccessTarget Target { get; } = target;

    /// <summary>Gets whether the access reads or changes the state.</summary>
    public JournaledStateAccessKind Kind { get; } = kind;

    /// <summary>Gets the operation.</summary>
    public JournaledStateOperation Operation { get; } = operation;
}

/// <summary>The result of a write which returns nothing.</summary>
internal readonly struct JournaledStateAccessNoResult;

/// <summary>The argument of an access which depends only on the state, or whose arguments a context doesn't examine.</summary>
internal readonly struct JournaledStateAccessNoArgument;

/// <summary>Describes the results of <see cref="JournaledStateOperation"/>s.</summary>
internal static class JournaledStateOperations
{
    /// <summary>
    /// Gets whether <paramref name="operation"/> returns a detached copy of the state's content, which differs in identity
    /// from every other result: an array which holds a snapshot of the entries, the keys or the values.
    /// </summary>
    public static bool ReturnsCopy(JournaledStateOperation operation) => operation is
        JournaledStateOperation.DictionarySnapshot
        or JournaledStateOperation.DictionaryKeys
        or JournaledStateOperation.DictionaryValues
        or JournaledStateOperation.ListSnapshot
        or JournaledStateOperation.QueueSnapshot
        or JournaledStateOperation.SetSnapshot
        or JournaledStateOperation.LogSnapshot;

    /// <summary>
    /// Gets whether <paramref name="operation"/> is a positional read: a read whose result depends only on its state's
    /// journaled content and on an argument which the serializer can encode, so the result can be evaluated again as of a
    /// position of the journal.
    /// </summary>
    /// <remarks>
    /// Writes, reads of what storage acknowledged (<see cref="JournaledStateOperation.LogCommittedSequence"/>), reads whose
    /// argument is an arbitrary sequence (the set comparisons), and enumerations of hash-based collections, whose order
    /// depends on the history of the collection's memory layout rather than on its journaled content, aren't positional.
    /// </remarks>
    public static bool IsPositional(JournaledStateOperation operation) => operation is
        JournaledStateOperation.ValueGet
        or JournaledStateOperation.CompletionSourceState
        or >= JournaledStateOperation.DictionaryGet and <= JournaledStateOperation.DictionaryCount
        or >= JournaledStateOperation.ListGet and <= JournaledStateOperation.ListSnapshot
        or >= JournaledStateOperation.QueueCount and <= JournaledStateOperation.QueueSnapshot
        or JournaledStateOperation.SetCount or JournaledStateOperation.SetContains
        or JournaledStateOperation.LogCount or JournaledStateOperation.LogFirstSequence or JournaledStateOperation.LogNextSequence
        or JournaledStateOperation.LogIsSealed or JournaledStateOperation.LogGet or JournaledStateOperation.LogTryGet
        or JournaledStateOperation.LogSnapshot;
}

/// <summary>Whether an access reads or changes journaled state.</summary>
/// <remarks>Records of accesses store these values.</remarks>
internal enum JournaledStateAccessKind : byte
{
    Read = 1,
    Write = 2,
}

/// <summary>The operations of journaled state which run through a <see cref="JournaledStateContext"/>.</summary>
/// <remarks>Records of accesses store these values, so existing values never change.</remarks>
internal enum JournaledStateOperation : byte
{
    ValueGet = 1,
    ValueSet = 2,

    DictionaryGet = 10,
    DictionaryTryGetValue = 11,
    DictionaryContainsKey = 12,
    DictionaryContainsPair = 13,
    DictionaryCount = 14,
    DictionarySnapshot = 15,
    DictionaryKeys = 16,
    DictionaryValues = 17,
    DictionarySet = 20,
    DictionaryAdd = 21,
    DictionaryRemove = 22,
    DictionaryRemovePair = 23,
    DictionaryClear = 24,

    ListGet = 30,
    ListCount = 31,
    ListContains = 32,
    ListIndexOf = 33,
    ListSnapshot = 34,
    ListSet = 40,
    ListAdd = 41,
    ListInsert = 42,
    ListRemove = 43,
    ListRemoveAt = 44,
    ListClear = 45,
    ListAddRange = 46,

    QueueCount = 50,
    QueuePeek = 51,
    QueueTryPeek = 52,
    QueueContains = 53,
    QueueSnapshot = 54,
    QueueEnqueue = 60,
    QueueDequeue = 61,
    QueueTryDequeue = 62,
    QueueClear = 63,

    SetCount = 70,
    SetContains = 71,
    SetSnapshot = 72,
    SetIsProperSubsetOf = 73,
    SetIsProperSupersetOf = 74,
    SetIsSubsetOf = 75,
    SetIsSupersetOf = 76,
    SetOverlaps = 77,
    SetEquals = 78,
    SetAdd = 80,
    SetRemove = 81,
    SetClear = 82,
    SetUnionWith = 83,
    SetIntersectWith = 84,
    SetExceptWith = 85,
    SetSymmetricExceptWith = 86,

    LogCount = 90,
    LogFirstSequence = 91,
    LogNextSequence = 92,
    LogCommittedSequence = 93,
    LogIsSealed = 94,
    LogGet = 95,
    LogTryGet = 96,
    LogSnapshot = 97,
    LogBeginDraft = 98,
    LogEpoch = 99,
    LogAppend = 100,
    LogTrimBefore = 101,
    LogClear = 102,
    LogSeal = 103,
    LogCommitDraft = 104,

    CompletionSourceState = 110,
    CompletionSourceTrySetResult = 111,
    CompletionSourceTrySetException = 112,
    CompletionSourceTrySetCanceled = 113,

    OutboxSend = 120,
    OutboxWithdraw = 121,
}
