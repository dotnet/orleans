# Journaled Todo List

This sample is an Aspire-hosted Blazor Web application which demonstrates durable,
journaled grain state using Orleans log-consistency providers. The app runs three
Orleans silos and uses emulated Azure Table Storage for clustering and Azure Blob
Storage for grain state.

## Run the sample

Install the .NET 10 SDK, the Aspire CLI, and a Docker-compatible container runtime.
From this directory, run:

```powershell
aspire run --project JournaledTodoList.AppHost
```

Open the `webapp` endpoint shown in the Aspire dashboard. Add, complete, and delete
items to exercise the journaled todo-list grain.
