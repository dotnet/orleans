# OrleansApp

This solution separates the application into four projects:

- `OrleansApp.Contracts` defines grain interfaces shared by callers and implementations.
- `OrleansApp.Grains` implements those grain interfaces.
- `OrleansApp.Silo` hosts the Orleans runtime and grain activations.
- `OrleansApp.Client` connects to the silo and calls a grain.

Build the solution:

```dotnetcli
dotnet build
```

Start the silo:

```dotnetcli
dotnet run --project OrleansApp.Silo
```

After the silo reports that it has started, run the client in another terminal:

```dotnetcli
dotnet run --project OrleansApp.Client
```

The client prints `Hello, friend!`.

The generated projects use localhost clustering for local development. Configure the silo and client with the same service ID, cluster ID, and production clustering provider before deploying them as separate processes.
