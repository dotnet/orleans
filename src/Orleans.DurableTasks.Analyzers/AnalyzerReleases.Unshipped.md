; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ORLEANS0100 | Usage | Warning | DurableTaskReplayAnalyzer, grain state accessed in code which a replay re-runs
ORLEANS0101 | Usage | Warning | DurableTaskReplayAnalyzer, non-deterministic value produced in code which a replay re-runs
ORLEANS0102 | Usage | Warning | DurableTaskReplayAnalyzer, external await in code which a replay re-runs
ORLEANS0103 | Usage | Warning | DurableTaskReplayAnalyzer, grain state changed before an external await
ORLEANS0104 | Usage | Warning | DurableTaskReplayAnalyzer, DurableTask.RunInline effect assigns a captured variable
ORLEANS0105 | Usage | Warning | DurableTaskDefinitionAnalyzer, durable task definition never awaited, scheduled, or posted
ORLEANS0106 | Usage | Warning | DurableTaskDefinitionAnalyzer, single-use durable task definition run more than once
ORLEANS0107 | Usage | Error | DurableTaskReplayAnalyzer, durable operation name contains '$'
ORLEANS0108 | Usage | Warning | DurableTaskReplayAnalyzer, durable operation name repeated at another call site
ORLEANS0109 | Usage | Warning | DurableTaskReplayAnalyzer, durable task body runs work off its scheduler
ORLEANS0110 | Usage | Warning | DurableTaskReplayAnalyzer, Task combinator runs durable work
ORLEANS0111 | Usage | Warning | DurableTaskReplayAnalyzer, non-durable delay in code which a replay re-runs
ORLEANS0112 | Usage | Info | DurableTaskReplayAnalyzer, cancellation handler without an IsTaskCancellationRequested filter
ORLEANS0113 | Usage | Warning | DurableTaskReplayAnalyzer, async void code in a durable task body
ORLEANS0114 | Usage | Info | DurableTaskReplayAnalyzer, repeated wait for one signal name
ORLEANS0115 | Usage | Info | DurableTaskReplayAnalyzer, log in code which a replay re-runs
ORLEANS0116 | Usage | Warning | DurableTaskDefinitionAnalyzer, durable task posted outside grains and durable task bodies
ORLEANS0117 | Usage | Warning | DurableTaskCleanupAnalyzer, task-cancellation cleanup passes the already-canceled CancellationToken
ORLEANS0118 | Usage | Warning | DurableTaskVersionAnalyzer, version check with a non-constant name or range
