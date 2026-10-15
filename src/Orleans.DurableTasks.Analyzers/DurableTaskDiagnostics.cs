using Microsoft.CodeAnalysis;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>Describes the replay-safety rules for durable task bodies.</summary>
internal static class DurableTaskDiagnostics
{
    private const string Category = "Usage";

    public static readonly DiagnosticDescriptor GrainStateInReplayedCode = Create(
        "ORLEANS0100",
        "Access grain state in recorded effects or after the last durable operation",
        "Access '{0}' in a DurableTask.Once effect or a step, or after the body's last durable operation: a replay re-runs this code with the state which later operations committed",
        DiagnosticSeverity.Warning,
        "Grain state holds every committed change, so code which a replay re-runs reads state which later operations wrote and applies its writes again. Read and change state in a DurableTask.Once effect, in a local step, or after the body's last durable operation, each of which runs once.");

    public static readonly DiagnosticDescriptor NonDeterministicValueInReplayedCode = Create(
        "ORLEANS0101",
        "Produce non-deterministic values in recorded effects",
        "Produce '{0}' in a DurableTask.Once effect, or read time from DurableTask.Current.UtcNow: a replay re-runs this code and observes another value",
        DiagnosticSeverity.Warning,
        "A replay re-runs this code, so a value which differs between runs changes the operations, identifiers, or arguments which the replay schedules. Produce the value in a DurableTask.Once effect, which records it, and read time from DurableTask.Current.UtcNow, the execution's logical time.");

    public static readonly DiagnosticDescriptor ExternalAwaitInReplayedCode = Create(
        "ORLEANS0102",
        "Await external operations in steps or after the last durable operation",
        "Await '{0}' in a step, such as DurableTask.Run or an async DurableTask method, or after the body's last durable operation: a replay runs it again",
        DiagnosticSeverity.Warning,
        "A replay re-runs every await between durable operations: it repeats the operation's effects, can observe another result, and can fail where the first run succeeded. Await the operation in DurableTask.Run or a local step, which records its result, or after the body's last durable operation.");

    public static readonly DiagnosticDescriptor StateChangeBeforeExternalAwait = Create(
        "ORLEANS0103",
        "Change grain state after the last external await",
        "Change '{0}' after the external await which follows it, or in a DurableTask.Once effect: the change commits without the body's result",
        DiagnosticSeverity.Warning,
        "A body's result commits with the state changes which its final synchronous code makes. A change followed by an external await can commit in an earlier journal write than the result, so a run which ends before the result commits applies the change again. Change state after the last external await, or in a DurableTask.Once effect.");

    public static readonly DiagnosticDescriptor OnceAssignsCapturedVariable = Create(
        "ORLEANS0104",
        "Return the outputs of a DurableTask.Once effect",
        "Return '{0}' from the effect instead of assigning it: a replay returns the recorded result without running the effect",
        DiagnosticSeverity.Warning,
        "A replay returns an effect's recorded result without running the effect, so assignments to the body's variables happen only in the run which runs it. Return every output from the effect, for example as a tuple or a record.");

    public static readonly DiagnosticDescriptor UnobservedDefinition = Create(
        "ORLEANS0105",
        "Await, schedule, or post durable task definitions",
        "Await, schedule, or post the durable task which '{0}' defines: a durable task runs only when it is awaited, scheduled, or posted",
        DiagnosticSeverity.Warning,
        "Calling a method which returns DurableTask creates a definition, and WithId, Detached, and Shielded return a configuration of it. The task runs when the definition is awaited, scheduled with ScheduleAsync, or started with Post.");

    public static readonly DiagnosticDescriptor ReusedDefinition = Create(
        "ORLEANS0106",
        "Run each durable task definition once",
        "Create a new definition for each run: the definition in '{0}' comes from an async DurableTask method, which runs once",
        DiagnosticSeverity.Warning,
        "An async DurableTask method returns a single-use definition, which throws InvalidOperationException when it runs a second time. Call the method again for each run, directly or through a factory.");

    public static readonly DiagnosticDescriptor ReservedCharacterInName = Create(
        "ORLEANS0107",
        "Use durable operation names without '$'",
        "Remove '$' from the name '{0}': generated identifiers use it",
        DiagnosticSeverity.Error,
        "Generated identifiers, such as $child-1, $once:pay, and reserve$2, contain '$', so the runtime rejects names with '$' which WithId and DurableTask.Once receive. Use another separator, such as '-'.");

    public static readonly DiagnosticDescriptor DuplicateName = Create(
        "ORLEANS0108",
        "Give each durable operation call site its own name",
        "Name this operation differently from the earlier '{0}': a reorder or a skipped call site changes which operation each recorded identifier holds",
        DiagnosticSeverity.Warning,
        "A name which one run repeats identifies later occurrences by their order, so a deployment which removes or reorders one call site changes which recorded operation the others reach. Give each call site its own name; a loop which repeats one call site keeps one name.");

    public static readonly DiagnosticDescriptor SchedulerEscape = Create(
        "ORLEANS0109",
        "Keep durable task bodies on their scheduler",
        "Run this work as durable children or on the body's scheduler instead of '{0}': work elsewhere schedules operations out of order and changes grain state concurrently",
        DiagnosticSeverity.Warning,
        "A durable task body runs on its grain activation's scheduler, which orders its operations and its state changes. Threads, timers, and thread-pool work run elsewhere, where DurableTask.Once and Post throw and state changes race with the grain's other work. Run the work in the body or in a step, or await a service which runs it.");

    public static readonly DiagnosticDescriptor NonDurableCombinator = Create(
        "ORLEANS0110",
        "Run concurrent durable work through durable combinators",
        "Use DurableTask.WhenAll, WhenAny, Race, or ScheduleAll instead of '{0}': their inputs keep stable identifiers when they run concurrently",
        DiagnosticSeverity.Warning,
        "Concurrent durable operations which a Task combinator starts are created in the order in which their code runs, which can differ on replay. DurableTask.WhenAll, WhenAny, Race, and ScheduleAll identify each input by its index and commit their invocations together.");

    public static readonly DiagnosticDescriptor NonDurableDelay = Create(
        "ORLEANS0111",
        "Wait with durable delays",
        "Use DurableTask.Delay or DurableTask.DelayUntil instead of '{0}': a replay waits for the whole duration again",
        DiagnosticSeverity.Warning,
        "Task.Delay and Thread.Sleep wait in memory: a deactivation loses the wait, and a replay waits the whole duration again. DurableTask.Delay and DurableTask.DelayUntil record their due time in logical time, so a replay resumes the wait.");

    public static readonly DiagnosticDescriptor UnfilteredCancellationHandler = Create(
        "ORLEANS0112",
        "Filter cancellation handlers by IsCancellationRequested",
        "Add 'when (DurableTask.Current.IsCancellationRequested)' to this handler: the host also stops a run by canceling it",
        DiagnosticSeverity.Info,
        "A host stops a run, for example when its activation deactivates, through the same OperationCanceledException as durable cancellation. A handler which filters by DurableTask.Current.IsCancellationRequested, or which rethrows, compensates for durable cancellation and lets a stopped run end.");

    public static readonly DiagnosticDescriptor AsyncVoidInBody = Create(
        "ORLEANS0113",
        "Return a task from asynchronous code in durable task bodies",
        "Return a Task or a DurableTask from '{0}': asynchronous void code runs outside the body's flow and failure handling",
        DiagnosticSeverity.Warning,
        "A durable task body observes the operations which it awaits. Asynchronous void code continues after the body has moved on, and its failures escape the body. Return a DurableTask or a Task, and await it.");

    public static readonly DiagnosticDescriptor RepeatedSignalWait = Create(
        "ORLEANS0114",
        "Derive a signal name for each repeated wait",
        "Wait for '{0}.For(...)' with a name for each iteration: a signal name latches one value, which every wait for it returns",
        DiagnosticSeverity.Info,
        "Each signal name of a task holds the first value which a sender delivers, and every wait for that name returns it. Derive a name for each occurrence with DurableSignal<T>.For, and send to the same derived name.");

    public static readonly DiagnosticDescriptor ReplayedLog = Create(
        "ORLEANS0115",
        "Log through a replay-safe logger",
        "Log through a logger which 'ReplaySafe()' returns: a replay re-runs this code and logs the event again",
        DiagnosticSeverity.Info,
        "A replay re-runs the code before the body's last durable operation, including its log calls. A logger which ReplaySafe() returns skips events while the task replays, so each event is logged once.");

    public static readonly DiagnosticDescriptor PostWithoutOutbox = Create(
        "ORLEANS0116",
        "Post durable tasks from grains or durable task bodies",
        "Schedule '{0}' with ScheduleAsync: Post stages the start in the calling grain's durable outbox, and code outside grains and durable task bodies, such as a client, has none",
        DiagnosticSeverity.Warning,
        "Post stages a start in the durable outbox of the grain which runs the calling code, and the start commits with that grain's next journal write. Code outside grains and durable task bodies, such as a client, has no outbox, so Post throws InvalidOperationException there. Schedule the task with ScheduleAsync, which completes once the task's host has accepted it.");

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
