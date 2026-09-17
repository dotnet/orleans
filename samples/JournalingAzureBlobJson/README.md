# Journaling with Azure Blob JSON

This sample demonstrates Orleans durable grain state journaled as JSON events in
Azure Blob Storage. It exercises durable dictionaries, lists, queues, sets, values,
task completion sources, and regular persistent state, then deactivates and
reactivates the grain to verify recovery.

The host's stopping token flows through grain calls, journal writes, and Azure
storage operations. Graceful shutdown uses the configured host shutdown deadline.

`JournaledSampleGrain` derives from `Grain` and injects `IDurableStateManager`.
Its constructor field initializers declare the seven named states using
`GetOrAddDictionary`, `GetOrAddList`, `GetOrAddQueue`, `GetOrAddSet`,
`GetOrAddValue`, `GetOrAddPersistentState`, and `GetOrAddTaskCompletionSource`.
The standard manager enrolls itself in the activation lifecycle during grain-bound
construction, before resolution returns. Orleans recovers those states at
`SetupState`, before `OnActivateAsync` and grain methods run. One awaited
`WriteStateAsync` acknowledges the pending changes across all seven states.

Declare new states during construction or synchronous activation setup, before
initialization; later `GetOrAdd` calls resolve existing names.
Keyed injection remains an equivalent way to obtain the same named object,
and `DurableGrain` remains a convenience base class. This sample preserves its
state names so journals written by the keyed-injection version remain readable.

## Run the sample

Install the .NET 10 SDK, the Aspire CLI, and a Docker-compatible container runtime.
From this directory, run:

```powershell
aspire run --project JournalingAzureBlobJson.AppHost
```

The application writes a scenario, verifies the recovered state, and prints the raw
JSON Lines journal stored by the Azure Storage emulator.

The manager API requires a Journaling package containing these APIs. In this
repository, `samples\Build-Samples.ps1` validates against packages built from the
current sources. The sample retains NuGet references and its own central package
file so it can be copied out unchanged once those packages are published.
