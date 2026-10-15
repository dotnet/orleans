using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Diagnostics;

/// <summary>
/// Provides the diagnostic listener and event payload types for the durable task runtime of grains.
/// </summary>
/// <remarks>
/// <para>
/// These types are public but may change between minor versions. They are intended for advanced scenarios such as
/// simulation testing and diagnostics.
/// </para>
/// <para>
/// Each event is written synchronously on the activation whose runtime makes the transition, when it stages the
/// transition, so an observer sees the runtime's state at that point; the change commits with the grain's next journal
/// write. An exception thrown by an observer propagates to the runtime operation which raised the event.
/// </para>
/// </remarks>
public static class DurableTaskEvents
{
    /// <summary>The name of the diagnostic listener for durable task runtime events.</summary>
    public const string ListenerName = "Orleans.DurableTasks";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>Gets an observable sequence of all durable task runtime events.</summary>
    public static IObservable<DurableTaskEvent> AllEvents { get; } = new Observable();

    internal static bool IsTaskCreatedEnabled => Listener.IsEnabled(nameof(TaskCreated));

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Event payloads are sealed types whose public fields and properties the generic annotation preserves, and subscribers consume the typed payload rather than reflecting over the members of the values it carries.")]
    private static void Write<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TEvent>(string name, TEvent payload)
        where TEvent : DurableTaskEvent
        => Listener.Write(name, payload);

    /// <summary>The base class used for durable task runtime diagnostic events.</summary>
    /// <param name="grainContext">The activation whose runtime raised the event.</param>
    /// <param name="taskId">The task which the event describes.</param>
    public abstract class DurableTaskEvent(IGrainContext grainContext, TaskId taskId)
    {
        /// <summary>The activation whose runtime raised the event.</summary>
        public readonly IGrainContext GrainContext = grainContext;

        /// <summary>The task which the event describes.</summary>
        public readonly TaskId TaskId = taskId;
    }

    /// <summary>Event payload for a task whose state the runtime staged: an invoked task, or an operation of an execution.</summary>
    /// <param name="grainContext">The activation whose runtime raised the event.</param>
    /// <param name="taskId">The created task.</param>
    public sealed class TaskCreated(IGrainContext grainContext, TaskId taskId) : DurableTaskEvent(grainContext, taskId)
    {
    }

    /// <summary>Event payload for a task whose terminal result the runtime staged.</summary>
    /// <param name="grainContext">The activation whose runtime raised the event.</param>
    /// <param name="taskId">The completed task.</param>
    /// <param name="response">The task's terminal result.</param>
    public sealed class TaskCompleted(IGrainContext grainContext, TaskId taskId, DurableTaskResponse response)
        : DurableTaskEvent(grainContext, taskId)
    {
        /// <summary>The task's terminal result.</summary>
        public readonly DurableTaskResponse Response = response;
    }

    /// <summary>
    /// Event payload for a task whose deletion the runtime staged, with its execution tree or when its retention ended.
    /// </summary>
    /// <param name="grainContext">The activation whose runtime raised the event.</param>
    /// <param name="taskId">The deleted task.</param>
    public sealed class TaskDeleted(IGrainContext grainContext, TaskId taskId) : DurableTaskEvent(grainContext, taskId)
    {
    }

    /// <summary>Event payload for an attempt of a task's body which the runtime started on the activation.</summary>
    /// <param name="grainContext">The activation whose runtime raised the event.</param>
    /// <param name="taskId">The task whose body runs.</param>
    public sealed class ExecutionStarted(IGrainContext grainContext, TaskId taskId) : DurableTaskEvent(grainContext, taskId)
    {
    }

    /// <summary>
    /// Event payload for an attempt of a task's body which ended on the activation: the body completed, or the runtime
    /// abandoned the attempt, for example because an abort or a deletion ended the task.
    /// </summary>
    /// <param name="grainContext">The activation whose runtime raised the event.</param>
    /// <param name="taskId">The task whose body ran.</param>
    public sealed class ExecutionStopped(IGrainContext grainContext, TaskId taskId) : DurableTaskEvent(grainContext, taskId)
    {
    }

    /// <summary>Event payload for an execution which started waiting for a signal which hasn't arrived.</summary>
    /// <param name="grainContext">The activation whose runtime raised the event.</param>
    /// <param name="taskId">The record which receives the signal's value.</param>
    public sealed class SignalWaitStarted(IGrainContext grainContext, TaskId taskId) : DurableTaskEvent(grainContext, taskId)
    {
    }

    /// <summary>
    /// Event payload for an execution's wait for a signal which ended: the signal's value committed, or the wait was
    /// canceled.
    /// </summary>
    /// <param name="grainContext">The activation whose runtime raised the event.</param>
    /// <param name="taskId">The record which receives the signal's value.</param>
    public sealed class SignalWaitEnded(IGrainContext grainContext, TaskId taskId) : DurableTaskEvent(grainContext, taskId)
    {
    }

    /// <summary>
    /// Event payload for a selection of the first task to complete, such as <c>DurableTask.WhenAny</c>'s, which found no
    /// completed candidate and waits for one.
    /// </summary>
    /// <param name="grainContext">The activation whose runtime raised the event.</param>
    /// <param name="taskId">The selection's decision, which records the winner.</param>
    public sealed class SelectionWaitStarted(IGrainContext grainContext, TaskId taskId) : DurableTaskEvent(grainContext, taskId)
    {
    }

    /// <summary>
    /// Event payload for an attempt of a task's body which replayed the operations which earlier attempts recorded, and
    /// started an operation which no earlier attempt recorded: <c>DurableTaskContext.IsReplaying</c> reports
    /// <see langword="false"/> from this point.
    /// </summary>
    /// <param name="grainContext">The activation whose runtime raised the event.</param>
    /// <param name="taskId">The task whose body runs.</param>
    public sealed class ReplayEnded(IGrainContext grainContext, TaskId taskId) : DurableTaskEvent(grainContext, taskId)
    {
    }

    internal static void EmitTaskCreated(IGrainContext grainContext, TaskId taskId)
    {
        if (!Listener.IsEnabled(nameof(TaskCreated)))
        {
            return;
        }

        Emit(grainContext, taskId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, TaskId taskId) =>
            Write(nameof(TaskCreated), new TaskCreated(grainContext, taskId));
    }

    internal static void EmitTaskCompleted(IGrainContext grainContext, TaskId taskId, DurableTaskResponse response)
    {
        if (!Listener.IsEnabled(nameof(TaskCompleted)))
        {
            return;
        }

        Emit(grainContext, taskId, response);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, TaskId taskId, DurableTaskResponse response) =>
            Write(nameof(TaskCompleted), new TaskCompleted(grainContext, taskId, response));
    }

    internal static void EmitTaskDeleted(IGrainContext grainContext, TaskId taskId)
    {
        if (!Listener.IsEnabled(nameof(TaskDeleted)))
        {
            return;
        }

        Emit(grainContext, taskId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, TaskId taskId) =>
            Write(nameof(TaskDeleted), new TaskDeleted(grainContext, taskId));
    }

    internal static void EmitExecutionStarted(IGrainContext grainContext, TaskId taskId)
    {
        if (!Listener.IsEnabled(nameof(ExecutionStarted)))
        {
            return;
        }

        Emit(grainContext, taskId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, TaskId taskId) =>
            Write(nameof(ExecutionStarted), new ExecutionStarted(grainContext, taskId));
    }

    internal static void EmitExecutionStopped(IGrainContext grainContext, TaskId taskId)
    {
        if (!Listener.IsEnabled(nameof(ExecutionStopped)))
        {
            return;
        }

        Emit(grainContext, taskId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, TaskId taskId) =>
            Write(nameof(ExecutionStopped), new ExecutionStopped(grainContext, taskId));
    }

    internal static void EmitSignalWaitStarted(IGrainContext grainContext, TaskId taskId)
    {
        if (!Listener.IsEnabled(nameof(SignalWaitStarted)))
        {
            return;
        }

        Emit(grainContext, taskId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, TaskId taskId) =>
            Write(nameof(SignalWaitStarted), new SignalWaitStarted(grainContext, taskId));
    }

    internal static void EmitSignalWaitEnded(IGrainContext grainContext, TaskId taskId)
    {
        if (!Listener.IsEnabled(nameof(SignalWaitEnded)))
        {
            return;
        }

        Emit(grainContext, taskId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, TaskId taskId) =>
            Write(nameof(SignalWaitEnded), new SignalWaitEnded(grainContext, taskId));
    }

    internal static void EmitSelectionWaitStarted(IGrainContext grainContext, TaskId taskId)
    {
        if (!Listener.IsEnabled(nameof(SelectionWaitStarted)))
        {
            return;
        }

        Emit(grainContext, taskId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, TaskId taskId) =>
            Write(nameof(SelectionWaitStarted), new SelectionWaitStarted(grainContext, taskId));
    }

    internal static void EmitReplayEnded(IGrainContext grainContext, TaskId taskId)
    {
        if (!Listener.IsEnabled(nameof(ReplayEnded)))
        {
            return;
        }

        Emit(grainContext, taskId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, TaskId taskId) =>
            Write(nameof(ReplayEnded), new ReplayEnded(grainContext, taskId));
    }

    private sealed class Observable : IObservable<DurableTaskEvent>
    {
        public IDisposable Subscribe(IObserver<DurableTaskEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<DurableTaskEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();

            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is DurableTaskEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
