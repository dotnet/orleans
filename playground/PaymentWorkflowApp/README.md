# Payment workflow runtime playground

This console playground explores the host-independent
`System.Distributed.DurableTasks` programming model with volatile and LiteDB job
storage implementations.

Run it with:

```powershell
dotnet run --project .\PaymentWorkflowApp.csproj
```

The scheduler validates hierarchical task identifiers, treats equivalent
identifier reuse idempotently, rejects conflicting reuse, preserves cancellation
intent, and resumes stored work after restart. External payment operations use
their task identifier as an idempotency key.
