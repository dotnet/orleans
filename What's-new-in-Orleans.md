---
layout: page
title: What's new in Orleans
---
{% include JB/setup %}

# Orleans Open Source v1.0 Update (January 2015)

Since the September 2014 Preview Update we have made a small number of public API changes, mainly related to clean up and more consistent naming. Those changes are summarized below:

### Public Type Names Changes

Old API   | New API
------------- | -------------
OrleansLogger | Logger
OrleansClient | GrainClient 
Grain.ActivateAsync | Grain.OnActivateAsync
Grain.DeactivateAsync | Grain.OnDeactivateAsync
Orleans.Host.OrleansSiloHost | Orleans.Runtime.Host.SiloHost 
Orleans.Host.OrleansAzureSilo | Orleans.Runtime.Host.AzureSilo
Orleans.Host.OrleansAzureClient| Orleans.Runtime.Host.zureClient
Orleans.Providers.IOrleansProvider | Orleans.Providers.IProvider
Orleans.Runtime.ActorRuntimeException | Orleans.Runtime.OrleansException
OrleansConfiguration | ClusterConfiguration
LoadAwarePlacementAttribute | ActivationCountBasedPlacementAttribute

### Other Changes

* All grain placement attribute (including [`StatelessWorker`]) now need to be defined on grain implementation class, rather than on grain interface.
* `LocalPlacementAttribute` was removed. There are now only `StatelessWorker` and `PreferLocalPlacement`.
* Support for Reactive programming with Async RX. 
* Orleans NuGet packages are now published on NuGet.org. 
  See this wiki page for advice on how to [convert legacy Orleans grain interface / class projects over to using NuGet packages](Convert-Orleans-v0.9-csproj-to-Use-v1.0-NuGet).



# September 2014 Preview Update

The preview of Orleans that was released in April 2014 has undergone some changes. Most of the modifications are in the form of bug fixes, some reported by preview users (such as a bug in the Reminder implementation), and some that we found ourselves.

We’ve taken feedback from developers internal to Microsoft, and feedback from external users. Not all the feedback has been incorporated yet, but some of it has.

Some of the changes are going to break existing code that you may have. There’s no ‘maybe’ here, it will break. Fortunately, almost all required changes to existing code are of a very mechanical nature, such as renaming a base class or adding/editing using statements. 

We also have a few new features added, which you will hopefully find useful or at least interesting enough to give us feedback on.

### Licensing
The license for the April bits contained a clause that prohibited use in a live operating environment. This clause has been removed in this update. We still consider this a preview release, which is made available "as is" for the purpose of evaluating the programming model and its applicability to your scenario(s).

If you want to do so in a live operating environment, that's your business, just as long as you understand that Microsoft makes no guarantees about its suitability.

### Azure SDK
The new preview has been built against version 2.4 of the Azure .NET SDK. Please update your installation to correspond, as Orleans needs the latest version of the Azure SDK.

### Properties in Grain Interfaces
A basic .NET API design guideline is that property implementations should not do I/O. Since using grain interfaces typically lead to network I/O, properties do not belong in such interfaces. Therefore, Orleans no longer supports them. Fortunately, fixing code that depends on this is straight-forward and mechanical, simply changing the property getter (setters were not supported previously, anyway) to a function:

For example:

``` csharp
public interface IGrain1 : Orleans.IGrain
{
  Task<string> SayHelloAsync(string greeting);
  Task<int> Count { get; }
}

would have to be changed to:

public interface IGrain1 : Orleans.IGrain
{
    Task<string> SayHelloAsync(string greeting);
    Task<int> GetCountAsync();
}
```


### Public Type Names
We determined that a lot of type names in the April release were not as descriptive as they could be, and many did not conform to common naming guidelines. We have corrected some of these, but not all. 

The most consequential of these is probably `GrainBase`, which was renamed `Grain`. This applies to both the non-generic and the generic variants of the type. This is the change that is guaranteed to affect all Orleans code bases.

We also determined that some types were not needed, and so we removed them. For example, when you create a timer using the April release, you get back an `IOrleansTimer` reference, which is just an `IDisposable` with no additional functionality, so it was removed and you now get just an `IDisposable`.

### Code Generation
Code generation has changed, mostly simplifications. While there used to be two approaches to code generation, one for assemblies with grain interfaces, one for assemblies with grains, there is now just one. Unfortunately, it means that you have to add an (empty) file `orleans.codegen.cs` under the ‘Properties’ folder of any grain assemblies that you have. The easiest way is to edit any existing project using Notepad and copying the Properties\AssemblyInfo.cs element, editing the name of the file.

On the positive side, though, VB and F# code generation has been improved. In the case of VB, you can now also define grain interfaces using VB. In the case of F#, you can now use F# to define grains with persisted state, which was challenging before.

### Namespaces
An issue (which, by the way, we knew about in April, but didn’t have time to address) was that the Orleans namespaces weren’t very well planned. Most public types were found in the root namespace, `Orleans`, which did not help developers find the most common types as quickly as we would like.

Therefore, a major overhaul of the namespaces has been done. Most of the types used by developers (`IGrain`, `Grain`, `IGrainState`, `IRemindable`, etc.) remaining in the root namespace, but many of the secondary types are now in more deeply nested namespaces.

For example, many of the attributes used to control concurrency (e.g. `ReentrantAttribute`, `ImmutableAttributes`) are now in the `Orleans.Concurrency` namespace. Placement-controlling attributes are in `Orleans.Placement`, while most other types that you would be like to need are now in `Orleans.Runtime`.

Many of these you don’t have to worry about if you are consistently using ‘var’ when declaring local variables. In most other cases, adding a ‘using Orleans.Runtime’ at the top of a file with errors will help.

### New Factory Methods
In the April preview, the factory methods for remote grain references were code-generated and you would find them by simple pattern matching.

For example, to create a reference to a grain of this interface:

``` csharp
public interface IGrain1 : Orleans.IGrain
```

 you would use the factory method ‘GetGrain’ on a static class created by the compiler:

``` csharp
gref = Grain1Factory.GetGrain(0);
```

This method is still available (but it may go away based on your feedback), but we have added another way, which is not directly dependent on code generation. Instead, it relies on further specification of the grain as having either a GUID key, an integer key, or a string key. This is done by using one of three new interfaces in place of `IGrain` when declaring a grain interfaces:

``` csharp
public interface IGrainWithGuidKey : IGrain
public interface IGrainWithIntegerKey : IGrain
public interface IGrainWithStringKey : IGrain
```

For example, `IGrain1`, which uses an integer key (but there’s currently no type safety around it), would be declared this way:

``` csharp
public interface IGrain1 : Orleans. IGrainWithIntegerKey
```

Doing so will allow the following to be used to create a grain reference:

``` csharp
gref = GrainFactory.GetGrain<IGrain1>(0);
```

Note that the new factory methods cannot be used for grain interfaces using ‘IGrain.’ Also note that the new methodology does not allow you to use an extended primary key, i.e. a tuple of GUID/Int64 and string.

We strongly encourage your feedback on this new way of doing things. We think the new methodology is more readable, more aligned with most other frameworks, and we think the type safety is valuable, but what matters are your thoughts.

There is also a generic version of `Cast`, also defined in `GrainFactory`, following a similar pattern.

### Non-Azure System Storage
In the April release, any reliable production-style deployment required using an Azure storage account to keep system state, specifically Orleans cluster status and the data used for the reminders functionality. In the September release, we added SQL Server as a possible location for that data, and this has impacted the server-side configuration.

If the server configuration file used to contain elements like this:

    <Globals>
        <Liveness LivenessType ="AzureTable" />
        <Azure DeploymentId="..." DataConnectionString="..."/>
    </Globals>

 It should now be:

    <Globals>
        <SystemStore SystemStoreType ="AzureTable" 
                     DeploymentId="..." 
                     DataConnectionString="..." />
    </Globals>

 If, instead, you want to use SQL Server, the configuration should look like this:

    <Globals>
        <SystemStore SystemStoreType ="SqlServer" 
                     DeploymentId="..." 
                     DataConnectionString="..." />
    </Globals>

Where the `DataConnectionString` is set to any valid SQL Server connection string. In order to use SQL Server as the store for system data, there’s now a script file `MembershipTableCreate.sql` in the `Binaries\OrleansServer` folder which establishes the necessary tables with the right schema. Make sure that all servers that will be hosting Orleans silos can reach the database and has access rights to it! We’ve tripped up a few times on this seemingly trivial concern, during our testing.

### F# support
F# support has actually improved somewhat in this release -- there is now a project template for building F# grain implementations. However, you may notice that the assembly containing support for serialization of F# lists has been removed (it was causing trouble for those developers who didn't have F# installed). We intend to publish it as a serialization sample. Sometime soon.

