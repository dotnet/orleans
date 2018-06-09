---
layout: page
title: Custom Grain Storage
---

# Custom Grain Storage

## Writing a Custom Grain Storage

In the tutorial on declarative actor storage, we looked at allowing grains to store their state in an Azure table using one of the built-in storage providers.
While Azure is a great place to squirrel away your data, there are many alternatives.
In fact, there are so many that there was no way to support them all.
Instead, Orleans is designed to let you easily add support for your own form of storage by writing a grain storage.

In this tutorial, we'll walk through how to write a simple file-based grain storage.
A file system is not necessarily the best place to store data for grains, since it's so local, but it's an easy example to help us illustrate the principles.

## Getting Started

An Orleans grain storage is a class that implements `IGrainStorage` which is included in [Microsoft.Orleans.Core NuGet package](https://www.nuget.org/packages/Microsoft.Orleans.Core/).

We also inherit from `ILifecycleParticipant<ISiloLifecycle>` which will allow us to subscribe to a particular event in the lifecycle of the silo.

We start by creating a class named `FileGrainStorage`.

```csharp
using Orleans;
using System;
using Orleans.Storage;
using Orleans.Runtime;
using System.Threading.Tasks;

namespace GrainStorage
{
    public class FileGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly string _storageName;
        private readonly FileGrainStorageOptions _options;
        private readonly ClusterOptions _clusterOptions;
        private readonly IGrainFactory _grainFactory;
        private readonly ITypeResolver _typeResolver;
        private JsonSerializerSettings _jsonSettings;

        public FileGrainStorage(string storageName, FileGrainStorageOptions options, IOptions<ClusterOptions> clusterOptions, IGrainFactory grainFactory, ITypeResolver typeResolver)
        {
            _storageName = storageName;
            _options = options;
            _clusterOptions = clusterOptions.Value;
            _grainFactory = grainFactory;
            _typeResolver = typeResolver;
        }

        public Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            throw new NotImplementedException();
        }

        public Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            throw new NotImplementedException();
        }

        public Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            throw new NotImplementedException();
        }
  
        public void Participate(ISiloLifecycle lifecycle)
        {
            throw new NotImplementedException();
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            throw new NotImplementedException();
        }
    }
}
```

Prior starting the implementation, we create an option class containing the root directory where the grains states files will be stored under. For that we will create an options file `FileGrainStorageOptions`:

```csharp
public class FileGrainStorageOptions
{
    public string RootDirectory { get; set; }
}
```

The  create a constructor containing two fields, `storageName` to specify which grains should write using this storage `[StorageProvider(ProviderName = "File")]` and `directory` which would be the directory where the grain states will be saved.

`IGrainFactory`, `ITypeResolver` and `JsonSerializerSettings` will be used in the next section where we will initilize the storage.

We also take two options as argument, our own `FileGrainStorageOptions` and the `ClusterOptions`. Those will be needed for the implementation of the storage functionalities.

## Initializing the storage

To initialize the storage, we register an `Init` function on the `ApplicationServices` lifecycle.

```csharp
public void Participate(ISiloLifecycle lifecycle)
{
    lifecycle.Subscribe(OptionFormattingUtilities.Name<FileGrainStorage>(_storageName), ServiceLifecycleStage.ApplicationServices, Init);
}
```

The `Init` function is used to set the `_jsonSettings` which will be used to configure the `Json` serializer. At the same time we create the folder to store the grains states if it does not exist yet.

```csharp
private Task Init(CancellationToken ct)
{
    // Settings could be made configurable from Options.
    _jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(OrleansJsonSerializer.GetDefaultSerializerSettings(_typeResolver, _grainFactory), false, false, null);

    var directory = new System.IO.DirectoryInfo(_rootDirectory);
    if (!directory.Exists)
        directory.Create();

    return Task.CompletedTask;
}
```

We also provide a common function to construct the filename ensuring uniqueness per service, grain Id and grain type.

```csharp
private string GetKeyString(string grainType, GrainReference grainReference)
{
    return $"{_clusterOptions.ServiceId}.{grainReference.ToKeyString()}.{grainType}";
}
```

## Reading State

To read a grain state, we get the filename using the function we previously defined and combine it to the root directory coming from the options.

```csharp
public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
{
    var fName = GetKeyString(grainType, grainReference);
    var path = Path.Combine(_options.RootDirectory, fName);

    var fileInfo = new FileInfo(path);
    if (!fileInfo.Exists)
        return;

    using (var stream = fileInfo.OpenText())
    {
        var storedData = await stream.ReadToEndAsync();
        grainState.State = JsonConvert.DeserializeObject(storedData, _jsonSettings);
    }
}
```

Note that for the deserialization, we use the `_jsonSettings` which was set on the `Init` function. This is important to be able to serialize/deserialize properly the state.

## Writing State

Writing the state is similar to reading the state.

```csharp
public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
{
    var storedData = JsonConvert.SerializeObject(grainState.State, _jsonSettings);

    var fName = GetKeyString(grainType, grainReference);
    var path = Path.Combine(_options.RootDirectory, fName);

    var fileInfo = new FileInfo(path);

    using (var stream = new StreamWriter(fileInfo.Open(FileMode.Create, FileAccess.Write)))
    {
        await stream.WriteAsync(storedData);
    }
}
```

Similarly as reading, we use `_jsonSettings` to write the state.

## Clearing State

Clearing the state would be deleting the file if the file exists.

```csharp
public Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
{
    var fName = GetKeyString(grainType, grainReference);
    var path = Path.Combine(_options.RootDirectory, fName);

    var fileInfo = new FileInfo(path);
    if (fileInfo.Exists)
        fileInfo.Delete();

    return Task.CompletedTask;
}
```

## Putting it Together

After that we will create a factory which will allow us to scope the options setting to the provider name and at the same time create an instance of the `FileGrainStorage` to ease the registration to the service collection.

```csharp
public static class FileGrainStorageFactory
{
    internal static IGrainStorage Create(IServiceProvider services, string name)
    {
        IOptionsSnapshot<FileGrainStorageOptions> optionsSnapshot = services.GetRequiredService<IOptionsSnapshot<FileGrainStorageOptions>>();
        return ActivatorUtilities.CreateInstance<FileGrainStorage>(services, name, optionsSnapshot.Get(name), services.GetProviderClusterOptions(name));
    }
}
```

Lastly to register the grain storage, we create an extension on the `ISiloHostBuilder` which internally register the grain storage as a named service using `.AddSingletonNamedService(...)`, an extension provided by `Orleans.Core`.

```csharp
public static class FileSiloBuilderExtensions
{
    public static ISiloHostBuilder AddFileGrainStorage(this ISiloHostBuilder builder, string providerName, Action<FileGrainStorageOptions> options)
    {
        return builder.ConfigureServices(services => services.AddFileGrainStorage(providerName, ob => ob.Configure(options)));
    }

    public static IServiceCollection AddFileGrainStorage(this IServiceCollection services, string providerName, Action<OptionsBuilder<FileGrainStorageOptions>> options)
    {
        options?.Invoke(services.AddOptions<FileGrainStorageOptions>(providerName));
        return services
            .AddSingletonNamedService(providerName, FileGrainStorageFactory.Create)
            .AddSingletonNamedService(providerName, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
    }
}
```

This enables us to add the file storage using the extension on the `ISiloHostBuilder`:

```csharp
var silo = new SiloHostBuilder()
    .UseLocalhostClustering()
    .AddFileGrainStorage("File", opts =>
    {
        opts.RootDirectory = "C:/TestFiles";
    })
    .Build();
```

Now we will be able to decorate our grains with the provider `[StorageProvider(ProviderName = "File")]` and it will store in the grain state in the root directory set in the options.