# Microsoft Orleans persistence for file storage

This package stores Orleans grain state as files in a configured directory. It is intended for local, single-silo development and testing. The provider does not replicate state or coordinate access through shared or network filesystems, so it is not production-ready.

## Install

```shell
dotnet add package Microsoft.Orleans.Persistence.FileStorage
```

## Configure the provider

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Persistence.FileStorage;

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddFileGrainStorage(
            providerName: "FileStorage",
            options => options.RootDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "Orleans",
                "GrainState"));
});

await builder.Build().RunAsync();
```

Use the same provider name when injecting persistent state:

```csharp
using Orleans.Runtime;

public sealed class MyGrain(
    [PersistentState("state", "FileStorage")]
    IPersistentState<MyGrainState> state) : Grain, IMyGrain
{
    public async Task SetData(string data)
    {
        state.State.Data = data;
        state.State.Version++;
        await state.WriteStateAsync();
    }

    public Task<string> GetData() => Task.FromResult(state.State.Data);
}

public sealed class MyGrainState
{
    public string Data { get; set; } = string.Empty;

    public int Version { get; set; }
}
```

The provider stores one binary file per grain state record and uses persisted ETags for basic optimistic concurrency checks. It does not coordinate multiple silos or replicate data.

For Orleans persistence concepts, see [Grain persistence](https://learn.microsoft.com/dotnet/orleans/grains/grain-persistence).
