# Microsoft.Orleans.DurableTasks.Testing

Replays the recorded histories of Microsoft Orleans durable tasks against new code in tests, so a change to a
workflow is checked against the executions which earlier code started before it's deployed.

```csharp
// Capture a grain's history from a test cluster, or from a deployment's journal storage.
var storage = cluster.GetSiloServiceProvider().GetRequiredService<IJournalStorageProvider>();
var history = await DurableTaskHistory.CaptureAsync(storage, grainId);
await history.SaveAsync("histories/order-42.json");

// Replay it against the current code.
await using var replayer = await DurableTaskReplayer.StartAsync(silo => silo.Services.AddSingleton<IShipping, FakeShipping>());
var result = await replayer.ReplayAsync(await DurableTaskHistory.LoadAsync("histories/order-42.json"));
result.EnsureSucceeded();
Assert.Equal(0, result[TaskId.CreateRoot("order-42")].Versions["carrier-booking"]);
```

- `DurableTaskHistory` holds a grain's committed journal: its tasks, their recorded operations and outcomes, and the
  grain's journaled state, in the journal format which recorded them. It is stored as a JSON document.
- `DurableTaskReplayer` hosts a dedicated in-process silo which replays one history at a time. Each pending execution
  replays until it completes, waits for a recorded operation, reaches an operation which the history doesn't record,
  or stalls. The silo's Durable Jobs clock stands before every recorded time, and it holds messaging wake-ups and
  deliveries, preserving pending inbox messages and outbox retries at their recorded state, including delays and
  delivery deadlines which have already passed.
- `DurableTaskReplayResult` reports, for each execution, its outcome, the recorded operations which it reached and
  skipped, the operation at which it stopped, what it waits for, and the versions which it follows.
  `EnsureSucceeded` throws with that report unless every execution replays consistently.

See [Test workflow changes](https://learn.microsoft.com/dotnet/orleans/grains/durable-tasks-testing) for the workflow.
