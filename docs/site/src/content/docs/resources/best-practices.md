---
title: Best practices in Orleans
description: Learn some of the best practices in Orleans for .NET Orleans app development.
ms.date: 07/03/2024
---

# Best practices in Orleans

Orleans was built to greatly simplify the building of distributed scalable applications, especially for the cloud. Orleans invented the Virtual Actor Model as an evolution of the Actor Model optimized for cloud scenarios.

Grains (virtual actors) are the base building blocks of an Orleans-based application. They encapsulate the state and behavior of application entities and maintain their lifecycle. The programming model of Orleans and the characteristics of its runtime fit some types of applications better than others. This document is intended to capture some of the tried and proven application patterns that work well in Orleans.

## Suitable apps

Consider Orleans when:

- Significant number (hundreds, millions, billions, and even trillions) of loosely coupled entities. To put the number in perspective, Orleans can easily create a grain for every person on Earth in a small cluster, so long as a subset of that total number is active at any point in time.
  - Examples: user profiles, purchase orders, application/game sessions, stocks.
- Entities are small enough to be single-threaded.
  - Example: Determine if the stock should be purchased based on the current price.
- Workload is interactive.
  - Example: request-response, start/monitor/complete.
- More than one server is expected or may be required.
  - Orleans runs on a cluster that is expanded by adding servers to expand the cluster.
- Global coordination is not needed or on a smaller scale between a few entities at a time.
  - Scalability and performance of execution are achieved by parallelizing and distributing a large number of mostly independent tasks with no single point of synchronization.

## Unsuitable apps

Orleans is not the best fit when:

- Memory must be shared between entities.
  - Each grain maintains its states and should not be shared.
- A small number of large entities may be multithreaded.
  - A microservice may be a better option when supporting complex logic in a single service.
- Global coordination and/or consistency are needed.
  - Such global coordination would severely limit the performance of an Orleans-based application. Orleans was built to easily scale to a global scale without the need for in-depth manual coordination.
- Operations that run for a long time.
  - Batch jobs, Single Instruction Multiple Data (SIMD) tasks.
  - This depends on the need of the application and may be a fit for Orleans.

## Grains overview

- Grains resemble objects. However, they are distributed, virtual, and asynchronous.
- They are loosely coupled, isolated, and primarily independent.
  - Each grain is encapsulated which also maintains its state independently of other grains.
  - Grains fail independently.
- Avoid chatty communication between grains.
  - Direct memory use is significantly less expensive than message passing.
  - Highly chatty grains may be better combined as a single grain.
  - Complexity/Size of arguments and serialization needs to be considered.
  - Deserializing twice may be more expensive than resending a binary message.
- Avoid bottleneck grains.
  - Single coordinator/Registry/Monitor.
  - Do staged aggregation if required.

### Asynchronicity

- No thread blocking: All items must be Async (Task Asynchronous Programming (TAP)).
- [await](/dotnet/csharp/programming-guide/concepts/async/) is the best syntax to use when composing async operations.
- Common Scenarios:
  - Return a concrete value:
  - `return Task.FromResult(value);`
  - Return a <xref:System.Threading.Tasks.Task> of the same type:
  - `return foo.Bar();`
  - `await` a <xref:System.Threading.Tasks.Task> and continue execution:

    ```csharp
    var x = await bar.Foo();

    var y = DoSomething(x);

    return y;
    ```

  - Fan-out:

    ```csharp
    var tasks = new List<Task>();

    foreach (var grain in grains)
    {
        tasks.Add(grain.Foo());
    }
    await Task.WhenAll(tasks);

    DoMoreWork();
    ```

### Implementation of grains

- Never perform a thread-blocking operation within a grain. All operations other than local computations must be explicitly asynchronous.
  - Examples: Synchronously waiting for an IO operation or a web service call, locking, running an excessive loop that is waiting for a condition, and so on.
- When to use a <xref:Orleans.Concurrency.StatelessWorkerAttribute>:
  - Functional operations such as decryption, decompression, and before forwarding for processing.
  - When only *local* grains are required in multiple activations.
  - Example: Performs well with staged aggregation within local silo first.
- Grains are non-reentrant by default.
  - Deadlock can occur due to call cycles.
  - Examples:
  - The grain calls itself.
  - Grain A calls B which calls C which in turn is calling A (A -> B -> C -> A).
  - Grain A calls Grain B as Grain B is calling Grain A (A -> B -> A).
  - Timeouts are used to automatically break deadlocks.
  - <xref:Orleans.Concurrency.ReentrantAttribute> can be used to allow the grain class reentrant.
  - Reentrant is still single-threaded however, it may interleave (divide processing/memory between tasks).
  - Handling interleaving increases risk by being error-prone.
- Inheritance:
  - Grain classes inherit from the Grain base class. Grain interfaces (one or more) can be added to each grain.
  - Disambiguation may be needed to implement the same interface in multiple grain classes.
- Generics are supported.

## Grain state persistence

Orleans' grain state persistence APIs are designed to be easy-to-use and provide
extensible storage functionality.

- <xref:Orleans.IGrainState?displayProperty=nameWithType> is extended by a .NET interface that contains fields that should be included in the grain's persisted state.
- Grains are persisted by using [IPersistentState\<TState\>](../grains/grain-persistence/index.md) is extended by the grain class that adds a strongly typed <xref:Orleans.Grain`1.State> property into the grain's base class.
- The initial <xref:Orleans.Grain`1.ReadStateAsync?displayProperty=nameWithType> automatically occurs before `ActiveAsync()` has been called for a grain.
- When the grain's state object's data is changed, then the grain should call <xref:Orleans.Grain`1.WriteStateAsync?displayProperty=nameWithType>.
  - Typically, grains call `State.WriteStateAsync()` at the end of grain method to return the Write promise.
  - The Storage provider *could* try to batch Writes that may increase efficiency, but behavioral contracts and configurations are orthogonal (independent) to the storage API used by the grain.
  - A **timer** is an alternative method to write updates periodically.
  - The timer allows the application to determine the amount of "eventual consistency"/statelessness allowed.
  - Timing (immediate/none/minutes) can also be controlled as to when to update.
  - <xref:Orleans.Runtime.PersistentStateAttribute> decorated classes, like other grain classes, can only be associated with one storage provider.
  - [StorageProvider(ProviderName = "name")](xref:Orleans.Providers.StorageProviderAttribute) attribute associates the grain class with a particular provider.
  - `<StorageProvider>` will need to be added to the silo config file which should also include the corresponding "name" from `[StorageProvider(ProviderName="name")]`.

## Storage providers

Built-in storage providers:

- Orleans.Storage houses all of the built-in storage providers.
- <xref:Orleans.Storage.MemoryStorage> (Data stored in memory without durable persistence) is used *only* for debugging and unit testing.

- AzureTableStorage:

  - Configure the Azure storage account information with an optional <xref:Orleans.Configuration.AzureTableStorageOptions.DeleteStateOnClear?displayProperty=nameWithType> (hard or soft deletions).
  - Orleans serializer efficiently stores JSON data in one Azure table cell.
  - Data size limit == max size of the Azure column which is 64kb of binary data.
  - Community-contributed code that extends the use of multiple table columns which increases the overall maximum size to 1MB.

### Storage provider debugging tips

- TraceOverride Verbose3 will log much more information about storage
    operations.
  - Update silo config file.
  - LogPrefix="Storage" for all providers, or specific type using "Storage.Memory" / "Storage.Azure" / "Storage.Shard".

How to deal with storage operation failures:

- Grains and storage providers can await storage operations and *retry* failures as needed.
- Unhandled failures will propagate back to the caller and will be seen by the client as a broken promise.
- Other than the initial read, there is not a concept that automatically destroys activations if a storage operation fails.
- Retrying failing storage is *not* a default feature for built-in storage providers.

### Grain persistence tips

Grain size:

- Optimal throughput is achieved by using *multiple smaller grains* rather than a few larger grains. However, the best practice of choosing a grain size and type is based on the *application domain model*.
  - Example: Users, Orders, etc.

External changing data:

- Grains can re-read the current state data from storage by using `State.ReadStateAsync()`.
- A timer can also be used to re-read data from storage periodically as well.
  - The functional requirements could be based on a suitable "staleness" of the information.
  - Example: Content cache grain.

- Adding and removing fields.
  - The storage provider will determine the effects of adding and removing additional fields from their persisted state.
  - Azure table does not support schemas and should automatically adjust to the additional fields.

Writing custom providers:

- Storage providers are simple to write which is also a significant extension element for Orleans.
- The API <xref:Orleans.GrainState> API contract drives the storage API contract (`Write`, `Clear`, <xref:Orleans.Grain`1.ReadStateAsync*>).
- The storage behavior is typically configurable (Batch writing, Hard or Soft Deletions, and so on) and defined by the storage provider.

## Cluster management

- Orleans automatically manages clusters.
  - Failed nodes --that is that can fail and join at any moment-- are automatically handled by Orleans.
  - The same silo instance table that is created for the clustering protocol can also be used for diagnostics. The table keeps a history of all of the silos in the cluster.
  - There are also configuration options of aggressive or more lenient failure detection.
- Failures can happen at any time and are a normal occurrence.
  - In the event a silo fails, the grains that were activated on the failed silo will automatically be reactivated later on other silos within the cluster.
  - Grains can timeout. A retry solution such as Polly can assist with retries.
  - Orleans provides a message delivery guarantee where each message is delivered at-most-once.
  - It is the responsibility of the caller to [retry](https://github.com/App-vNext/Polly/wiki/Retry) any failed calls if needed.
  - Common practice is to retry from end-to-end from the client/front end.

## Deployment and production management

Scaling out and in:

- Monitor the Service-Level Agreement (SLA)
- Add or Remove instances
- Orleans automatically rebalances and takes advantage of the new hardware. However, activated grains are not rebalanced when a new silo is added to the cluster.

## Logging and testing

- Logging, Tracing, and Monitoring:
  - Inject logging using dependency injection:

    ```csharp
    public HelloGrain(ILogger<HelloGrain> logger)
    {
        _logger = logger;
    }
    ```

  - [Microsoft.Extensions.Logging](/dotnet/api/microsoft.extensions.logging) is utilized for functional and flexible logging.

Testing:

- `Microsoft.Orleans.TestingHost` NuGet package contains <xref:Orleans.TestingHost.TestCluster> which can be used to create an in-memory cluster, comprised of two silos by default, which can be used to test grains.
- For more information, see [Unit testing with Orleans](../implementation/testing.md).

Troubleshooting:

- Use Azure table-based membership for development and testing.
  - Works with Azure Storage Emulator for local troubleshooting.
  - OrleansSiloInstances table displays the state of the cluster.
  - Use unique deployment Ids (partition keys) to keep it simple.
- Silo isn't starting.
  - Check OrleansSiloInstances to determine if the silo registered there.
  - Make sure that the firewall is open for TCP ports: 11111 and 30000.
  - Check the logs, including the extra log that contains startup errors.
- Frontend (Client) cannot connect to the silo cluster.
  - The client must be hosted in the same service as the silos.
  - Check OrleansSiloInstances to make sure the silos (gateways) are registered.
  - Check the client log to make sure that the gateways match the ones listed in the OrleansSiloInstances' table.
  - Check the client log to validate that the client was able to connect to one or more of the gateways.
