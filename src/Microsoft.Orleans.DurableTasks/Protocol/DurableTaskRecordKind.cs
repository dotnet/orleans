using System.Text.Json.Serialization;

namespace Orleans.DurableTasks.Protocol;

/// <summary>
/// The kind of operation which a stored task records, which the runtime sets when it creates the record.
/// </summary>
/// <remarks>
/// A replay compares the operation which it requests under an identifier with the recorded kind, so an operation of
/// another kind diverges, which stalls the attempt, instead of observing or changing the other operation's record. The
/// kind changes only when an invocation arrives for an <see cref="EarlyCancellation"/> record, which becomes its
/// <see cref="Invocation"/>. JSON journals store the kind by name.
/// </remarks>
[GenerateSerializer]
[Alias("Orleans.DurableTasks.Protocol.DurableTaskRecordKind")]
[JsonConverter(typeof(JsonStringEnumConverter<DurableTaskRecordKind>))]
internal enum DurableTaskRecordKind : byte
{
    /// <summary>A task which a client or a grain scheduled as a request, which owns its execution tree.</summary>
    Invocation,

    /// <summary>A cancellation which arrived before the invocation of its task.</summary>
    EarlyCancellation,

    /// <summary>A durable execution's call to another grain.</summary>
    RemoteCall,

    /// <summary>A durable execution's local child, such as a <c>DurableTask.Run</c> step.</summary>
    LocalStep,

    /// <summary>A durable delay.</summary>
    Delay,

    /// <summary>The recorded outcome of a <c>DurableTask.RunInline</c> effect.</summary>
    Effect,

    /// <summary>A version decision.</summary>
    Version,

    /// <summary>A signal's value, beneath the invoked task which received it.</summary>
    SignalValue,

    /// <summary>A durable execution's send of a signal.</summary>
    SignalSend,

    /// <summary>A selection of the first task to complete among a combinator's candidates.</summary>
    Selection,

    /// <summary>A subscription to an existing task's result.</summary>
    Subscription,

    /// <summary>A start of a detached task, which subscribes to the task's result.</summary>
    DetachedStart,
}
