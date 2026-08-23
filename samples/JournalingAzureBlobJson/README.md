# Journaling with Azure Blob JSON

This sample demonstrates Orleans durable grain state journaled as JSON events in
Azure Blob Storage. It exercises durable dictionaries, lists, queues, sets, values,
task completion sources, and regular persistent state, then deactivates and
reactivates the grain to verify recovery.

## Run the sample

Install the .NET 10 SDK, the Aspire CLI, and a Docker-compatible container runtime.
From this directory, run:

```powershell
aspire run --project JournalingAzureBlobJson.AppHost
```

The application writes a scenario, verifies the recovered state, and prints the raw
JSON Lines journal stored by the Azure Storage emulator.

The AppHost starts Azurite and injects the `blobs` connection into the Orleans
application. The application creates the configured blob container, and the Orleans
Azure Blob journal provider owns the write-ahead log and checkpoint blobs used to
recover grain state.
