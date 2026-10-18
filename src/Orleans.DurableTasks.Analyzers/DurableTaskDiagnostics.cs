using Microsoft.CodeAnalysis;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>Describes the replay-safety rules for durable task bodies.</summary>
internal static class DurableTaskDiagnostics
{
    private const string Category = "Usage";

    public static readonly DiagnosticDescriptor GrainStateInReplayedCode = Create(
        "ORLEANS0200",
        "Access grain state in recorded effects or after the last durable operation",
        "Access '{0}' in a DurableTask.RunInline effect or a step, or after the body's last durable operation: a replay re-runs this code with the state which later operations committed",
        DiagnosticSeverity.Warning,
        "Grain state holds every committed change, so code which a replay re-runs reads state which later operations wrote and applies its writes again. Read and change state in a DurableTask.RunInline effect, in a local step, or after the body's last durable operation, each of which runs once.");

    public static readonly DiagnosticDescriptor NonDeterministicValueInReplayedCode = Create(
        "ORLEANS0201",
        "Produce non-deterministic values in recorded effects",
        "Produce '{0}' in a DurableTask.RunInline effect, or read time from DurableTask.Current.UtcNow: a replay re-runs this code and observes another value",
        DiagnosticSeverity.Warning,
        "A replay re-runs this code, so a value which differs between runs changes the operations, identifiers, or arguments which the replay schedules. Produce the value in a DurableTask.RunInline effect, which records it, and read time from DurableTask.Current.UtcNow, the execution's logical time.");

    public static readonly DiagnosticDescriptor ExternalAwaitInReplayedCode = Create(
        "ORLEANS0202",
        "Await external operations in steps or after the last durable operation",
        "Await '{0}' in a step, such as DurableTask.Run or an async DurableTask method, or after the body's last durable operation: a replay runs it again",
        DiagnosticSeverity.Warning,
        "A replay re-runs every await between durable operations: it repeats the operation's effects, can observe another result, and can fail where the first run succeeded. Await the operation in DurableTask.Run or a local step, which records its result, or after the body's last durable operation.");

    public static readonly DiagnosticDescriptor StateChangeBeforeExternalAwait = Create(
        "ORLEANS0203",
        "Change grain state after the last external await",
        "Change '{0}' after the external await which follows it, or in a DurableTask.RunInline effect: the change commits without the body's result",
        DiagnosticSeverity.Warning,
        "A body's result commits with the state changes which its final synchronous code makes. A change followed by an external await can commit in an earlier journal write than the result, so a run which ends before the result commits applies the change again. Change state after the last external await, or in a DurableTask.RunInline effect.");

    public static readonly DiagnosticDescriptor RunInlineAssignsCapturedVariable = Create(
        "ORLEANS0204",
        "Return the outputs of a DurableTask.RunInline effect",
        "Return '{0}' from the effect instead of assigning it: a replay returns the recorded result without running the effect",
        DiagnosticSeverity.Warning,
        "A replay returns an effect's recorded result without running the effect, so assignments to the body's variables happen only in the run which runs it. Return every output from the effect, for example as a tuple or a record.");

    public static readonly DiagnosticDescriptor UnobservedDefinition = Create(
        "ORLEANS0205",
        "Await, schedule, or post durable task definitions",
        "Await, schedule, or post the durable task which '{0}' defines: a durable task runs only when it is awaited, scheduled, or posted",
        DiagnosticSeverity.Error,
        "Calling a method which returns DurableTask creates a definition, and WithId, Detached, and Shielded return a configuration of it. The task runs when the definition is awaited, scheduled with ScheduleAsync, or started with Post. A definition which a statement discards, or which a local holds without awaiting, scheduling, posting, returning, or passing it on, never runs.");

    public static readonly DiagnosticDescriptor ReusedDefinition = Create(
        "ORLEANS0206",
        "Run each durable task definition once",
        "Create a new definition for each run: the definition in '{0}' comes from an async DurableTask method, which runs once",
        DiagnosticSeverity.Warning,
        "An async DurableTask method returns a single-use definition, which throws InvalidOperationException when it runs a second time. Call the method again for each run, directly or through a factory.");

    public static readonly DiagnosticDescriptor ReservedCharacterInName = Create(
        "ORLEANS0207",
        "Use durable operation names without '$'",
        "Remove '$' from the name '{0}': generated identifiers use it",
        DiagnosticSeverity.Error,
        "Generated identifiers, such as $child-1 and reserve$2, contain '$', so the runtime rejects names with '$' which WithId and DurableTask.RunInline receive. Use another separator, such as '-'.");

    public static readonly DiagnosticDescriptor DuplicateName = Create(
        "ORLEANS0208",
        "Give each durable operation call site its own name",
        "Name this operation differently from the earlier '{0}': a reorder or a skipped call site changes which operation each recorded identifier holds",
        DiagnosticSeverity.Warning,
        "A name which one run repeats identifies later occurrences by their order, so a deployment which removes or reorders one call site changes which recorded operation the others reach. Give each call site its own name; a loop which repeats one call site keeps one name.");

    public static readonly DiagnosticDescriptor SchedulerEscape = Create(
        "ORLEANS0209",
        "Keep durable task bodies on their scheduler",
        "Run this work as durable children or on the body's scheduler instead of '{0}': work elsewhere schedules operations out of order and changes grain state concurrently",
        DiagnosticSeverity.Warning,
        "A durable task body runs on its grain activation's scheduler, which orders its operations and its state changes. Threads, timers, and thread-pool work run elsewhere, where DurableTask.RunInline and Post throw and state changes race with the grain's other work. Run the work in the body or in a step, or await a service which runs it.");

    public static readonly DiagnosticDescriptor NonDurableCombinator = Create(
        "ORLEANS0210",
        "Run concurrent durable work through durable combinators",
        "Use DurableTask.WhenAll, WhenAny, Race, or ScheduleAll instead of '{0}': their inputs keep stable identifiers when they run concurrently",
        DiagnosticSeverity.Warning,
        "Concurrent durable operations which a Task combinator starts are created in the order in which their code runs, which can differ on replay. DurableTask.WhenAll, WhenAny, Race, and ScheduleAll identify each input by its index and commit their invocations together.");

    public static readonly DiagnosticDescriptor NonDurableDelay = Create(
        "ORLEANS0211",
        "Wait with durable delays",
        "Use DurableTask.Delay or DurableTask.DelayUntil instead of '{0}': a replay waits for the whole duration again",
        DiagnosticSeverity.Warning,
        "Task.Delay and Thread.Sleep wait in memory: a deactivation loses the wait, and a replay waits the whole duration again. DurableTask.Delay and DurableTask.DelayUntil record their due time in logical time, so a replay resumes the wait.");

    public static readonly DiagnosticDescriptor UnfilteredCancellationHandler = Create(
        "ORLEANS0212",
        "Filter cancellation handlers by IsTaskCancellationRequested",
        "Add 'when (DurableTask.Current.IsTaskCancellationRequested)' to this handler: attempt cancellation, such as a stopping activation, also throws OperationCanceledException",
        DiagnosticSeverity.Info,
        "A durable task observes two kinds of cancellation through OperationCanceledException. Task cancellation is durable: a caller requests it, the host records it, and the body decides the task's outcome, for example by compensating. Attempt cancellation stops the current run, for example when its activation deactivates, and the run's later durable operations throw. A handler which filters by DurableTask.Current.IsTaskCancellationRequested, or which rethrows, compensates for task cancellation and lets a canceled attempt end.");

    public static readonly DiagnosticDescriptor AsyncVoidInBody = Create(
        "ORLEANS0213",
        "Return a task from asynchronous code in durable task bodies",
        "Return a Task or a DurableTask from '{0}': asynchronous void code runs outside the body's flow and failure handling",
        DiagnosticSeverity.Warning,
        "A durable task body observes the operations which it awaits. Asynchronous void code continues after the body has moved on, and its failures escape the body. Return a DurableTask or a Task, and await it.");

    public static readonly DiagnosticDescriptor RepeatedSignalWait = Create(
        "ORLEANS0214",
        "Derive a signal name for each repeated wait",
        "Wait for '{0}.For(...)' with a name for each iteration: a signal name latches one value, which every wait for it returns",
        DiagnosticSeverity.Info,
        "Each signal name of a task holds the first value which a sender delivers, and every wait for that name returns it. Derive a name for each occurrence with DurableSignal<T>.For, and send to the same derived name.");

    public static readonly DiagnosticDescriptor ReplayedLog = Create(
        "ORLEANS0215",
        "Log through a replay-safe logger",
        "Log through a logger which 'ReplaySafe()' returns: a replay re-runs this code and logs the event again",
        DiagnosticSeverity.Info,
        "A replay re-runs the code before the body's last durable operation, including its log calls. A logger which ReplaySafe() returns skips events while the task replays, so each event is logged once.");

    public static readonly DiagnosticDescriptor PostWithoutOutbox = Create(
        "ORLEANS0216",
        "Post durable tasks from grains or durable task bodies",
        "Schedule '{0}' with ScheduleAsync: Post stages the start in the calling grain's durable outbox, and code outside grains and durable task bodies, such as a client, has none",
        DiagnosticSeverity.Warning,
        "Post stages a start in the durable outbox of the grain which runs the calling code, and the start commits with that grain's next journal write. Code outside grains and durable task bodies, such as a client, has no outbox, so Post throws InvalidOperationException there. Schedule the task with ScheduleAsync, which completes once the task's host has accepted it.");

    public static readonly DiagnosticDescriptor CanceledTokenInCleanup = Create(
        "ORLEANS0217",
        "Pass AttemptCancellationToken to calls in task-cancellation cleanup",
        "Pass AttemptCancellationToken instead of CancellationToken: this code runs after task cancellation, which has already canceled CancellationToken, so the call ends at once",
        DiagnosticSeverity.Warning,
        "DurableTaskContext.CancellationToken is canceled when task cancellation is requested and when the host cancels the attempt. Code which runs only after task cancellation, such as a catch clause filtered by IsTaskCancellationRequested, receives a canceled token, so the cleanup calls which observe it end at once. DurableTaskContext.AttemptCancellationToken is canceled only when the host cancels the attempt, so cleanup calls which receive it run to completion and still end promptly when the activation stops.");

    public static readonly DiagnosticDescriptor NonConstantVersion = Create(
        "ORLEANS0218",
        "Pass constant version names and ranges",
        "Pass a constant '{0}' to '{1}': an execution records its version under the change's name, and each deployment states the versions which it supports",
        DiagnosticSeverity.Warning,
        "An execution records the version of each change which it checks under the change's name, and every replay of the execution reads it back under the same name. A name which depends on data can differ between replays and deployments, which records a second decision. A deployment states which versions its code supports, so the range is a constant of the code which branches on it.");

    public static readonly DiagnosticDescriptor UnobservedHandle = Create(
        "ORLEANS0219",
        "Await, combine, or return the handles of scheduled children",
        "Await the handle of '{0}', pass it to a combinator, or return it: the execution owns this child, nothing observes its outcome, and deleting the execution's tree cancels it",
        DiagnosticSeverity.Info,
        "In a durable task body, ScheduleAsync starts an attached child and returns its handle. The execution owns the child: the child's result and failure reach only code which observes the handle, and deleting the execution's tree, once the execution is terminal, cancels a child which is still running. Await the handle, pass it to a combinator such as ScheduledTask.WhenAll, or return it. Work which outlives the execution starts with Detached().");

    public static readonly DiagnosticDescriptor UnnamedOperation = Create(
        "ORLEANS0220",
        "Name durable operations whose position can change",
        "Name this operation, with WithId or a DurableTask.RunInline name: its generated identifier counts the unnamed operations before it, which loops, conditions, and deployments change",
        DiagnosticSeverity.Info,
        "An unnamed child, effect, detached start, or combinator takes the execution's next generated identifier, such as $child-3 or $when-all-2, which counts the unnamed operations of its kind which ran before it. An operation in a loop or a condition, or after another unnamed operation, can receive another identifier in a replay or after a deployment which adds or removes an operation before it, and then diverges from the recorded execution. A name which WithId or DurableTask.RunInline receives identifies the operation by its call site.");

    public static readonly DiagnosticDescriptor SynchronousStep = Create(
        "ORLEANS0221",
        "Run synchronous work with DurableTask.RunInline",
        "Run this delegate with DurableTask.RunInline: it neither awaits nor observes its CancellationToken, so a step adds a child execution and a journal write without running asynchronous work",
        DiagnosticSeverity.Warning,
        "DurableTask.Run runs its delegate as a child execution when the step is awaited, possibly in a later turn, and commits the result in a journal write of its own. DurableTask.RunInline runs synchronous work in the current work item and records its result with the execution's next journal write, together with the state changes which it makes, so consecutive effects commit together. An execution which recorded the step diverges when it replays an effect in its place, so a deployment with pending executions branches the change on DurableTask.GetVersion.");

    public static readonly DiagnosticDescriptor AsyncVoidDelegate = Create(
        "ORLEANS0222",
        "Pass delegates which return a task for asynchronous work",
        "Return a Task from '{0}': '{1}' records an async void delegate's completion at its first await, before its work completes",
        DiagnosticSeverity.Error,
        "An async void delegate returns to its caller at its first incomplete await. DurableTask.RunInline and DurableTask.Run record the delegate's completion when it returns, so the record precedes the work which follows the await, a replay doesn't run that work, and its failures escape the task. Run asynchronous work in DurableTask.Run with a delegate which returns a Task, or in an async DurableTask method.");

    public static readonly DiagnosticDescriptor StateChangedInPlace = Create(
        "ORLEANS0223",
        "Assign new values to grain state instead of changing them in place",
        "Assign a new value to '{0}' instead of changing the object which it holds: grain state records each change which passes through its API, and a change in place bypasses it",
        DiagnosticSeverity.Warning,
        "Journaled grain state records each change which passes through its API, such as an assignment to IDurableValue<T>.Value or IPersistentState<T>.State, or to an entry of a durable dictionary. A change made in place to an object which the state holds, such as an item added to a list in the value or a property set on an entry, bypasses the API: no record holds it until another change writes the whole value, so the state in memory differs from the state which the journal recovers. Store immutable values, such as records and immutable collections, and assign a new value to change one.");

    private static DiagnosticDescriptor Create(
        string id,
        string title,
        string messageFormat,
        DiagnosticSeverity severity,
        string description) =>
        new(
            id,
            title,
            messageFormat,
            Category,
            severity,
            isEnabledByDefault: true,
            description,
            helpLinkUri: GetHelpLink(id));

    /// <summary>
    /// Returns the help link of a diagnostic: the Orleans diagnostics index, whose anchor for the diagnostic links to
    /// its page. The Orleans analyzers use the same form.
    /// </summary>
    internal static string GetHelpLink(string id) => $"https://aka.ms/orleans/diagnostics#{id.ToLowerInvariant()}";
}
