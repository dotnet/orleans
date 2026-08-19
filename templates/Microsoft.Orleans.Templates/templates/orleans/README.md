# OrleansApp

This solution separates the application into five projects:

- `OrleansApp.AppHost` orchestrates the application and its Azurite resources.
- `OrleansApp.Contracts` defines grain interfaces shared by callers and implementations.
- `OrleansApp.Grains` implements those grain interfaces.
- `OrleansApp.Silo` hosts the Orleans runtime and grain activations.
- `OrleansApp.Client` connects to the silo and calls a grain.

Build the solution:

```dotnetcli
dotnet build
```

Start the application:

```dotnetcli
dotnet run --project OrleansApp.AppHost
```

The AppHost starts Azurite, the silo, and the external client. Open the Aspire dashboard using the URL printed in the terminal to inspect the resources and their logs.

The client prints `Hello, friend! Call count: 1.`. The grain stores its call count in Azure Blob Storage, and both the silo and client discover the cluster through Azure Table Storage. The AppHost runs both services through Azurite for local development.

A container runtime must be running so that Aspire can start Azurite. When the application is published, configure the Azure Storage resource for the target environment.
