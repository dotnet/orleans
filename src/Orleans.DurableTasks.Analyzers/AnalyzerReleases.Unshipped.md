; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ORLEANS0200 | Usage | Warning | DurableTaskReplayAnalyzer, grain state accessed in code which a replay re-runs
ORLEANS0201 | Usage | Warning | DurableTaskReplayAnalyzer, non-deterministic value produced in code which a replay re-runs
ORLEANS0202 | Usage | Warning | DurableTaskReplayAnalyzer, external await in code which a replay re-runs
ORLEANS0203 | Usage | Warning | DurableTaskReplayAnalyzer, grain state changed before an external await
ORLEANS0204 | Usage | Warning | DurableTaskReplayAnalyzer, DurableTask.RunInline effect assigns a captured variable
ORLEANS0205 | Usage | Error | DurableTaskDefinitionAnalyzer, durable task definition never awaited, scheduled, or posted
ORLEANS0206 | Usage | Warning | DurableTaskDefinitionAnalyzer, single-use durable task definition run more than once
ORLEANS0207 | Usage | Error | DurableTaskReplayAnalyzer, durable operation name contains '$'
ORLEANS0208 | Usage | Warning | DurableTaskReplayAnalyzer, durable operation name repeated at another call site
ORLEANS0209 | Usage | Warning | DurableTaskReplayAnalyzer, durable task body runs work off its scheduler
ORLEANS0210 | Usage | Warning | DurableTaskReplayAnalyzer, Task combinator runs durable work
ORLEANS0211 | Usage | Warning | DurableTaskReplayAnalyzer, non-durable delay in code which a replay re-runs
ORLEANS0212 | Usage | Info | DurableTaskReplayAnalyzer, cancellation handler without an IsTaskCancellationRequested filter
ORLEANS0213 | Usage | Warning | DurableTaskReplayAnalyzer, async void code in a durable task body
ORLEANS0214 | Usage | Info | DurableTaskReplayAnalyzer, repeated wait for one signal name
ORLEANS0215 | Usage | Info | DurableTaskReplayAnalyzer, log in code which a replay re-runs
ORLEANS0216 | Usage | Warning | DurableTaskDefinitionAnalyzer, durable task posted outside grains and durable task bodies
ORLEANS0217 | Usage | Warning | DurableTaskCleanupAnalyzer, task-cancellation cleanup passes the already-canceled CancellationToken
ORLEANS0218 | Usage | Warning | DurableTaskVersionAnalyzer, version check with a non-constant name or range
ORLEANS0219 | Usage | Info | DurableTaskDefinitionAnalyzer, handle of a scheduled child which nothing observes
ORLEANS0220 | Usage | Info | DurableTaskIdentityAnalyzer, unnamed durable operation whose generated identifier can shift
ORLEANS0221 | Usage | Warning | DurableTaskDelegateAnalyzer, DurableTask.Run step which runs synchronous work
ORLEANS0222 | Usage | Error | DurableTaskDelegateAnalyzer, async void delegate passed to DurableTask.Run or DurableTask.RunInline
ORLEANS0223 | Usage | Warning | DurableTaskStateMutationAnalyzer, grain state value changed in place
