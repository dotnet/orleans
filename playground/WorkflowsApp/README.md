# Durable workflows playground

This playground exercises Orleans durable tasks, durable messaging, and
journaled state using several workflow patterns.

Run the app host:

```powershell
dotnet run --project .\WorkflowsApp.AppHost\WorkflowsApp.AppHost.csproj
```

The examples rely on these runtime outcomes:

- workflow identifiers are stable across scheduling, polling, and cancellation;
- conflicting reuse of an identifier is rejected;
- handler state, outgoing messages, and inbox completion commit atomically;
- polling timeouts do not cancel workflow execution;
- cancellation intent and remote delivery survive activation restart;
- request context is restored when a workflow resumes after recovery.

The implementations are experimental and are intended for runtime development
and design validation.
