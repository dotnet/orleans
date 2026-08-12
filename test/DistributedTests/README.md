# Distributed Tests

The projects in this directory are Crank workloads, not `dotnet test` test
projects. The repository-level [`distributed-tests.yml`](../../distributed-tests.yml)
file defines the workloads and scenarios.

## Prerequisites

- Windows. The current Crank configuration launches the Windows app hosts
  (`DistributedTests.Server.exe` and `DistributedTests.Client.exe`).
- The .NET SDK selected by the repository's `global.json`.
- Docker Desktop or another Docker-compatible runtime for
  [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite),
  unless you use an Azure Storage account.
- Ports `5010`, `11111`, and `30000` available locally, plus ports `10000`,
  `10001`, and `10002` when using Azurite.

Install the [Crank](https://github.com/dotnet/crank) controller and agent using
the same version:

```powershell
dotnet tool install --global Microsoft.Crank.Controller --version "0.2.0-*"
dotnet tool install --global Microsoft.Crank.Agent --version "0.2.0-*"
```

If either tool is already installed, replace `install` with `update`.

## Run locally

Run all commands from the repository root unless stated otherwise.

### 1. Start Azurite

The distributed tests use Azure Tables for cluster membership and Azure Queues
for the control channel required by every scenario. The reliability and rolling
scenarios also use queues to coordinate their workloads. Start the same Azurite
version and service endpoints used by CI:

```powershell
docker run --detach --rm --name orleans-distributed-tests-azurite `
  --publish 10000:10000 `
  --publish 10001:10001 `
  --publish 10002:10002 `
  mcr.microsoft.com/azure-storage/azurite:3.35.0 `
  azurite `
  --blobHost 0.0.0.0 `
  --queueHost 0.0.0.0 `
  --tableHost 0.0.0.0 `
  --skipApiVersionCheck
```

The `local` profile passes the loopback queue and table endpoints to each
workload. Loopback endpoints select Azurite's
`UseDevelopmentStorage=true` connection string, so no credentials or
connection string configuration is required.

### 2. Build the workloads

The Crank configuration defaults to `net8.0` and reads the built applications
from `Artifacts\DistributedTests`:

```powershell
dotnet build test\DistributedTests\DistributedTests.Server\DistributedTests.Server.csproj --configuration Release --framework net8.0
dotnet build test\DistributedTests\DistributedTests.Client\DistributedTests.Client.csproj --configuration Release --framework net8.0
```

Rebuild both projects after changing Orleans or the distributed-test workloads.

### 3. Start the Crank agent

Start the agent in a separate terminal and leave it running:

```powershell
crank-agent --url http://localhost:5010
```

The local profile sends the server, client, and chaos-agent jobs to this
endpoint. It also reduces the server workload to one local instance; the
non-local scenario definitions default to ten server instances.

### 4. Run a scenario

In another terminal, run:

```powershell
crank --config .\distributed-tests.yml --scenario ping --profile local
```

Available scenarios are `ping`, `fanout`, `streaming`, `reliability`, and
`rolling`. Their workload sizes and durations are defined in
[`distributed-tests.yml`](../../distributed-tests.yml), and some run for
several minutes.

When the run finishes, stop the Crank agent and Azurite:

```powershell
docker stop orleans-distributed-tests-azurite
```

## Use an Azure Storage account

To use Azure Storage instead of Azurite, keep the local profile for its Crank
agent endpoint and override its storage variables:

```powershell
$account = "<storage-account-name>"
crank --config .\distributed-tests.yml --scenario ping --profile local `
  --variable "azureQueueUri=https://$account.queue.core.windows.net" `
  --variable "azureTableUri=https://$account.table.core.windows.net"
```

When `TENANT_ID` and `CLIENT_ID` are set, the workload processes authenticate
non-loopback endpoints using `ClientAssertionCredential`; otherwise they use
`DefaultAzureCredential`. Configure the selected credential on the agent host
and grant that identity permission to create and use queues and tables in the
target account.

## Troubleshooting

- **Crank reports that `http://localhost:5010` is unavailable**: start the
  Crank agent and verify that no other process is using port `5010`.
- **The source folder or executable is missing**: build both workloads in
  `Release` for `net8.0` and confirm that the executable exists under
  `Artifacts\DistributedTests\<workload>\net8.0`.
- **The workload cannot connect to storage**: confirm that the Azurite container
  is running and that ports `10001` and `10002` are reachable.
