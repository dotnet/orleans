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
        "Grain state holds every committed change, so code which a replay re-runs reads state which later operations wrote and applies its writes again. A recorded effect, a step, or the code after the body's last durable operation reads and changes state once.");

    public static readonly DiagnosticDescriptor NonDeterministicValueInReplayedCode = Create(
        "ORLEANS0101",
        "Produce non-deterministic values in recorded effects",
        "Produce '{0}' in a DurableTask.Once effect, or read time from DurableTask.Current.UtcNow: a replay re-runs this code and observes another value",
        DiagnosticSeverity.Warning,
        "Code which a replay re-runs computes the values which decide its durable operations from arguments, recorded results, and logical time. DurableTask.Once records a value which differs between runs, and DurableTask.Current.UtcNow is the execution's logical time.");

    public static readonly DiagnosticDescriptor ExternalAwaitInReplayedCode = Create(
        "ORLEANS0102",
        "Await external operations in steps or after the last durable operation",
        "Await '{0}' in a step, such as DurableTask.Run or an async DurableTask method, or after the body's last durable operation: a replay runs it again",
        DiagnosticSeverity.Warning,
        "A replay re-runs every await between durable operations: it repeats the operation's effects, can observe another result, and can fail where the first run succeeded. A step records its result, so a replay returns it.");

    public static readonly DiagnosticDescriptor StateChangeBeforeExternalAwait = Create(
        "ORLEANS0103",
        "Change grain state after the last external await",
        "Change '{0}' after the external await which follows it, or in a DurableTask.Once effect: the change commits without the body's result",
        DiagnosticSeverity.Warning,
        "A body's result commits with the state changes which its final code makes. A change followed by an external await can commit in an earlier write than the result, so a run which ends before the result commits applies it again.");

    public static readonly DiagnosticDescriptor OnceAssignsCapturedVariable = Create(
        "ORLEANS0104",
        "Return the outputs of a DurableTask.Once effect",
        "Return '{0}' from the effect instead of assigning it: a replay returns the recorded result without running the effect",
        DiagnosticSeverity.Warning,
        "A replay returns an effect's recorded result without running the effect, so assignments to the body's variables happen only in the run which runs it.");

    public static readonly DiagnosticDescriptor UnobservedDefinition = Create(
        "ORLEANS0105",
        "Await or schedule durable task definitions",
        "Await, schedule, or detach the durable task which '{0}' defines: a durable task runs only when it is awaited, scheduled, or detached",
        DiagnosticSeverity.Warning,
        "Calling a method which returns DurableTask creates a definition. The definition runs when it is awaited, scheduled with ScheduleAsync, or started with DetachAsync.");

    public static readonly DiagnosticDescriptor ReusedDefinition = Create(
        "ORLEANS0106",
        "Run each durable task definition once",
        "Create a new definition for each run: the definition in '{0}' comes from an async DurableTask method, which runs once",
        DiagnosticSeverity.Warning,
        "An async DurableTask method returns a single-use definition. Calling the method again creates a definition for another run.");

    public static readonly DiagnosticDescriptor ReservedCharacterInName = Create(
        "ORLEANS0107",
        "Use durable operation names without '$'",
        "Remove '$' from the name '{0}': generated identifiers use it",
        DiagnosticSeverity.Error,
        "Generated identifiers, such as $child-1, $once:pay, and reserve$2, contain '$', so names which WithId and DurableTask.Once receive don't.");

    public static readonly DiagnosticDescriptor DuplicateName = Create(
        "ORLEANS0108",
        "Give each durable operation call site its own name",
        "Name this operation differently from the earlier '{0}': a reorder or a skipped call site changes which operation each recorded identifier holds",
        DiagnosticSeverity.Warning,
        "A name which one run repeats identifies later occurrences by their order. Distinct call sites with distinct names keep their recorded identifiers when a deployment reorders them or a run skips one of them.");

    public static readonly DiagnosticDescriptor SchedulerEscape = Create(
        "ORLEANS0109",
        "Keep durable task bodies on their scheduler",
        "Run this work as durable children or on the body's scheduler instead of '{0}': work elsewhere schedules operations out of order and changes grain state concurrently",
        DiagnosticSeverity.Warning,
        "A durable task body runs on its grain activation's scheduler, which orders its operations and its state changes. Threads, timers, and thread-pool work run elsewhere.");

    public static readonly DiagnosticDescriptor NonDurableCombinator = Create(
        "ORLEANS0110",
        "Run concurrent durable work through durable combinators",
        "Use DurableTask.WhenAll, WhenAny, Race, or ScheduleAll instead of '{0}': their inputs keep stable identifiers when they run concurrently",
        DiagnosticSeverity.Warning,
        "Concurrent durable operations which a Task combinator starts receive generated identifiers in the order in which they begin, which can differ on replay. Durable combinators identify each input by its index.");

    public static readonly DiagnosticDescriptor NonDurableDelay = Create(
        "ORLEANS0111",
        "Wait with durable delays",
        "Use DurableTask.Delay or DurableTask.DelayUntil instead of '{0}': a replay waits for the whole duration again",
        DiagnosticSeverity.Warning,
        "A durable delay records its due time in logical time, so a replay resumes it. Other waits restart on every run.");

    public static readonly DiagnosticDescriptor UnfilteredCancellationHandler = Create(
        "ORLEANS0112",
        "Filter cancellation handlers by IsCancellationRequested",
        "Add 'when (DurableTask.Current.IsCancellationRequested)' to this handler: the host also stops a run by canceling it",
        DiagnosticSeverity.Info,
        "A host drains and aborts a run, for example when its activation deactivates, through the same OperationCanceledException as durable cancellation. A handler which filters by IsCancellationRequested compensates for durable cancellation only, and lets an aborted run stop.");

    public static readonly DiagnosticDescriptor AsyncVoidInBody = Create(
        "ORLEANS0113",
        "Return a task from asynchronous code in durable task bodies",
        "Return a Task or a DurableTask from '{0}': asynchronous void code runs outside the body's flow and failure handling",
        DiagnosticSeverity.Warning,
        "A durable task body observes the operations it awaits. Asynchronous void code continues after the body has moved on, and its failures escape the body.");

    public static readonly DiagnosticDescriptor RepeatedSignalWait = Create(
        "ORLEANS0114",
        "Derive a signal name for each repeated wait",
        "Wait for '{0}.For(...)' with a name for each iteration: a signal name latches one value, which every wait for it returns",
        DiagnosticSeverity.Info,
        "Each signal name of a task holds the first value which a sender delivers. A wait in a loop derives a distinct name for each occurrence with DurableSignal<T>.For.");

    public static readonly DiagnosticDescriptor ReplayedLog = Create(
        "ORLEANS0115",
        "Log through a replay-safe logger",
        "Log through a logger which 'ReplaySafe()' returns: a replay re-runs this code and logs the event again",
        DiagnosticSeverity.Info,
        "A logger which ReplaySafe() returns skips events while the task replays, so each event is logged once.");

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
            helpLinkUri: $"https://aka.ms/orleans/diagnostics#{id.ToLowerInvariant()}");
}
