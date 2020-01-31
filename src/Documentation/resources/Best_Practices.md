# Best Practices

Orleans was designed in 2014 with the intention of removing low-level
abstractions from actor model frameworks. Grains (actors stored in memory) are
used to maintain the lifecycle among other features. Orleans is based on the
actor model and fits some projects better that others.

## Orleans should be considered when:

-   Significant number (hundreds to millions) of loosely coupled entities

    -   Example: Internet of Things (IoT) – individual devices turning on/off

-   Entities are small enough to be single-threaded

    -   Example: Determine if stock should be purchased based on current price

-   Workload is interactive

    -   Example: request-response, start/monitor/complete

-   More than one server is expected or may be required\\

    -   Orleans runs on a cluster which is expanded by adding servers to expand
        the cluster

-   Global coordination is not needed or on a smaller scale between a few
    entities at a time

    -   Orleans maintains its clusters and talks to other clusters. Each cluster
        is able to communicate through tcp/ip

-   *Various entities are used at different times*

    -   This depends on the entities that are being used

## Orleans is not the best fit when:

-   Memory must be shared between entities

    -   Each grain maintains its own states and should not be shared.

-   A small number of large entities that may be multithreaded

    -   A microservice may be a better option when supporting complex logic in a
        single service

-   Global coordination and/or consistency is needed

    -   Orleans maintains the silos within its own framework

-   *Operations that run for a long time*

    -   Batch jobs, Single Instruction Multiple Data (SIMD) tasks

    -   This depends on the need of the application and may be a fit for Orleans

## Grains

**Overview**:

-   Actors are not objects, however they are similar

-   They are loosely coupled, isolated, and primarily independent

    -   Each grain is encapsulated which also maintains its own state
        independently of other grains

    -   Grains fail independently

-   Avoid chatty communication between grains

    -   Direct memory use is significantly less expensive than message passing

    -   Highly chatty grains may be better combined as a single grain

    -   Complexity/Size of arguments and serialization need to be considered

        -   Deserializing twice may be more expensive than resending a binary
            message

-   Avoid bottleneck grains

    -   Single coordinator/Registry/Monitor

    -   Do staged aggregation If required

**Asynchronicity**:

-   No thread blocking: All items must be Async (Task Asynchronous Programming
    (TAP))

-   [await](https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/concepts/async/)
    is the best syntax to use when composing async operations

-   Common Scenarios:

    -   Return a concrete value:

        -   return Task.FromResult(value);

    -   Return a Task of the same type:

        -   return foo.Bar();

    -   Await a Task and continue execution:

        -   `var x = await bar.Foo();  
            var y = DoSomething(x);  
            return y;`

    -   Fan-out:

        -   `var tasks = new List\<Task\>();  
            foreach(var grain in grains)  
            { tasks.Add(grain.Foo()) }  
            await Task.WhenAll(tasks);  
            DoMoreWork();`

**Implementation of Grains**:

-   When to use a [StatelessWorker]

    -   Functional operations such as: decryption, decompression, and before
        forwarding for processing

    -   When only *local* grains are required in multiple activations

    -   Example: Performs well with staged aggregation within local silo first

-   Grains are non-reentrant by default

    -   Deadlock can occur due to call cycles

        -   Example: The grain calls itself

    -   Timeouts are used to automatically break deadlocks

    -   Attribute [Reentrant] can be used to allow the grain class reentrant

    -   Reentrant is still single-threaded however, it may interleave (divide
        processing/memory between tasks)

    -   Handling interleaving increases risk by being error prone

-   Inheritance

    -   Inheriting grain interfaces is easy

    -   Disambiguation may be needed to implement the same interface in multiple
        grain glasses

-   Generics are supported

## Grain State Persistence

Orleans’ grain state persistence APIs are designed to be easy-to-use and provide
extensible storage functionality.

-   Tutorial: *Needs to be created*

**Overview**:

-   Orleans.IGrainState is extended by a .NET interface which contains fields
    that should be included in the grain’s persisted state.

-   GrainBase\<T\> is extended by the grain class that adds a strongly typed
    State property into the grain’s base class.

-   The initial State.ReadStateAsync() automatically occurs prior to
    ActiveAsync() has been called for a grain.

-   When the grain’s state object’s data is changed, then the grain should call
    State.WriteStateAsync()

    -   Typically, grains call State.WriteStateAsync() at the end of grain
        method to return the Write promise.

    -   The Storage provider *could* try to batch Writes that may increase
        efficiency, but behavioral contract and configurations are orthogonal
        (independent) to the storage API used by the grain.

    -   A **timer** is an alternative method to write updates periodically.

        -   The timer allows the application to determine the amount of
            “eventual consistency”/statelessness allowed.

        -   Timing (immediate/none/minutes) can also be controlled as to when to
            update.

    -   Each grain class can only be associated with one storage provider.

        -   [StorageProvider(ProviderName=”name”)] attribute associates the
            grain class with a particular provider

        -   \<StorageProvider\> will need to be added to the Silo config file
            which should also include the corresponding “name” from
            [StorageProvider(ProviderName=”name”)]

        -   A composite storage provider can be used with SharedStorageProvider

## Storage Providers

Built-in Storage Providers

-   Orleans.Storage houses all of the built-in storage providers. The namespace
    is: OrleansProviders.dll

-   MemoryStorage (Data stored in memory without durable persistence) is used
    *only* for debugging and unit testing.

-   AzureTableStorage

    -   Configure the Azure storage account information with an optional
        DeleteStateOnClear (hard or soft deletions)

    -   Orleans serializer efficiently stores binary data in one Azure table
        cell

    -   Data size limit == max size of the Azure column which is 64kb of binary
        data

    -   Community contributed code that extends the use of multiple table
        columns which increases the overall maximum size to 1mb.

-   SharedStorageProvider will write data to multiple underlying storage
    providers by using the grain id hash.

    -   Example: *need to create*

Storage Provider Debugging Tips

-   TraceOverride Verbose3 will log much more information about storage
    operations.

    -   Update silo config file

        -   LogPrefix=”Storage” for all providers, or specific type using
            “Storage.Memory” / ”Storage.Azure” / “Storage.Shard”

How to deal with Storage Operation Failures

-   Grains and storage providers can await storage operations and *retry*
    failures as needed

-   Unhandled failures will propagate back to the caller and will be seen by the
    client as a broken promise

-   Other than the initial read, there is not a concept that automatically
    destroys activations if a storage operation fails

-   Retrying a failing storage is *not* a default feature for built-in storage
    providers

Grain Persistence Tips

Grain Size

-   Optimal throughput is achieved by using *multiple smaller grains* rather
    than a few larger grains. However, the best practice of choosing a grain
    size and type is base on the *application domain model*.

    -   Example: Users, Orders, etc.

External Changing Data

-   Grain are able to re-read the current state data from storage by using
    State.ReadStateAsyc()

-   A timer can also be used to re-read data from storage periodically as well

    -   The functional requirements could be based on a suitable “staleness” of
        the information

        -   Example: Content Cache Grain

-   Adding and Removing Fields

    -   The storage provider will determine the effects of adding and removing
        additional fields from its persisted state.

    -   Azure table does not support schemas and should automatically adjust to
        the additional fields.

Writing Custom Providers

-   Storage providers are simple to write which is also a significant extension
    element for Orleans

    -   Tutorial: *need tutorial*

-   The API GrainState API contract drives the storage API contract (Write,
    Clear, ReadStateAsync())

-   The storage behavior is typically configurable (Batch writing, Hard or Soft
    Deletions, etc.) and defined by the storage provider

## Cluster Management

-   Orleans automatically manages clusters

    -   Worker roles (silo) can fail and join at any time which is handled
        automatically

    -   A silo instance table exists for diagnostics

    -   There are also configuration options of an aggressive or a more lenient
        failure detection

-   Failures can happen at any time and are a normal occurrence

    -   Lost grains are automatically reactivated

    -   Grains that are making in-process calls can fail or timeout

    -   Orleans provides it best effort for grain delivery

    -   Any network message can be lost and should be retired by the application
        if it is crucial

        -   Common practice is to retry from end-to-end from the client/front
            end

-   Currently there is not a graceful shutdown

    -   A Azure upgrade or reboot is treated as a node failure

## Deployment and Production Management

Service Monitoring

-   Orleans provided information

    -   Windows performance counters

    -   Compact Azure metrics table

    -   Extremely detailed Azure statistics table

    -   Trace specific logging events

-   Create a custom performance counters

Scaling out and in

-   Monitor the Service-Level Agreement (SLA)

-   Add or Remove instances

-   Orleans automatically rebalances and takes advantage of the new hardware

## Logging and Testing

-   Logging, Tracing, and Monitoring

    -   GrainBase.GetLogger() exposes Info(), Warn(), Error(), Verbose()

    -   .NET tracing and Runtimes trace to a local file by default

    -   Azure Diagnostics is also easily consumed

    -   Logger.LogConsumer.Add() allows the developer to choose their own
        logging consumer

    -   Default tracing levels can be overridden for grains and runtime
        components

        -   Example: `<TraceLevelOverrideLogPrefix="Application"
            TraceLevel=“Verbose">`

        -   Example: `<TraceLevelOverrideLogPrefix=“Runtime"
            TraceLevel=“Warning">`

Testing

-   UnitTestingBase class is used for unit testing

-   Starting two or more silos in app domains and a client in the main app
    domain

-   Simplifies the versions of what is used internally

Troubleshooting

-   Logs (Silo and Frontend logging)

    -   WAD may not pick up the logging in the event of a startup failure

    -   Remote Desktop Protocol (RDP) to the machines for verification

-   Use Azure table-based membership for development and testing

    -   Works with Azure Storage Emulator for local troubleshooting

    -   OrleansSiloInstances table displays the state of the cluster

    -   Use unique deployment Ids (partition keys) in order to keep it simple

-   Silo isn’t starting

    -   Check OrleansSiloInstances to determine if the silo registered there.

    -   Make sure that firewall is open for TCP ports: 11111 and 30000

    -   Check the logs and don’t forget that there is an extra log for startup
        errors

    -   RDP into the server or worker role instance and attempt to start it
        manually

-   Frontend (Client) cannot connect to the silo cluster

    -   The client must be hosted in the same service as the silos

    -   Check OrleansSiloInstances to make sure the silos (gateways) are
        registered

    -   Check the client log to make sure that the gateways match the ones
        listed in the OrleansSiloInstances’ table

    -   Check the client log to validate that the client was able to connect to
        one or more of the gateways
