---
layout: page
title: Orleans Streams Extensibility
---
{% include JB/setup %}

There is a number of ways developers can extend the currently implemented behaviour of Orleans Streaming. We will describe those below, starting from the simplest and following with more complicated cases. Please read the [Orleans Streams Implementation](Streams-Implementation) before reading this section to have ahigh level view of the internal implementation.

## Stream Provider Configuration

Currently implemented stream providers (Simple Message Stream Provider and Persistent Stream Providers) support a number of configuration options.

**Simple Message Stream Provider Configuration**:

SMS Stream Provider currently supports only a single configuration option:

1. **FireAndForgetDelivery**: this option specifies if the messages sent by SMS stream producer are sent as fire and forget without the way to know if they were delivered or not. When FireAndForgetDelivery is set to false (messages are sent not as FireAndForget), the stream producer's call `stream.OnNext()` returns a Task that represents the processing status of the stream consumer. If this Task succeeds, the producer knows for sure that the message was delivered and processed succesfully. If FireAndForgetDelivery is set to true, the returned Task only expresses that the Orleans runtime has accepted the message and queued it for further delivery. The default value for FireAndForgetDelivery is false. 

**Persistent Stream Provider Configuration**:

All persistent stream providers support the following configuration options:

1. **GetQueueMessagesTimerPeriod** - how much time the pulling agents wait after the last attempt to pull from the queue that did not return any itimes before the agent attemps to pull again. Default is 100 milliseconds.
2. **InitQueueTimeout** - how much time the pulling agents waits for the adapter to initialize the connection with the queue. Default is 5 seconds.
3. **QueueBalancerType** - the type of balancing algorithm to be used to balance queues to silos and agents. Default is ConsistentRingBalancer.

**Azure Queue Stream Provider Configuration**:

Azure Queue stream provider supports the following configuration options, in addition to what is supported by Persistent Stream Provider:

1. **DataConnectionString** - the Azure Queue storage connection string.
2. **DeploymentId** - the deployment id of this Orlean cluster (usualy similar to Azure Deployment Id).
3. **CacheSize** - the size of the persistent provider cache that is used to store stream message for further delivery. Default is 4096.

It would be totaly possible and a lot of times easy to provide additional configuration options. For example, in some scenarios developers might want more control over  queue names used by the Queue Adapter. This is currently abstracted away with [`IStreamQueueMapper`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IStreamQueueMapper.cs), but there is currently no way to configure which `IStreamQueueMapper` to use without writing a new code. We would be happy to provide such an option, if needed. So please consider adding more configuration options to existing stream providers before writing a compeletely new  provider.


## Writing Custom Queue Adapter

If you want to use a different queueing technology, you need to write a queue adapter that abstracts away the access to that queue. Below we provide details on how this should be done. Please refer to [`AzureQueueAdapterFactory`](https://github.com/dotnet/orleans/blob/master/src/OrleansProviders/Streams/AzureQueue/AzureQueueAdapterFactory.cs) for an example.

1. Start by defining a `MyQueueFactory` class that implements [**`IQueueAdapterFactory`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IQueueAdapterFactory.cs). You need to:

     a. Initialize the factory: read the passed config values, potentially allocate some data structures if you need to, etc.
     
     b. Implement a method that returns your `IQueueAdapter`.
     
     c. Implement a method that returns `IQueueAdapterCache`. Theoretically, you can build your own `IQueueAdapterCache`, but you dont have to. It is a good idea just to allocate and return an Orleans `SimpleQueueAdapterCache`.
     
     d. Implement a method that returns `IStreamQueueMapper`. Again, it is theoretically possible to build your own `IStreamQueueMapper`, but you dont have to. It is a good idea just to allocate and return an Orleans `HashRingBasedStreamQueueMapper`. 

2. Implement `MyQueueAdapter` class that implements the [**`IQueueAdapter`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IQueueAdapter.cs) interface, which is an interfaces that manages access to a **sharded queue**. `IQueueAdapter` manages access to a set of queues/queue partitions (those are the queues that were returned by `IStreamQueueMapper`). It provides an ability to enqueue a message in a specified the queue and create an `IQueueAdapterReceiver` for a particular queue.

3. Implement `MyQueueAdapterReceiver` class that implements the [**`IQueueAdapterReceiver`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IQueueAdapterReceiver.cs), which is an interfaces that manages access to **one queue (one queue partition)**. In addition to initilaiztion and shutwodn, it basicaly provides one method: retreave up to maxCount messages from the queue.

4. Declare `public class MyQueueStreamProvider : PersistentStreamProvider<MyQueueFactory>`. This is your new Stream Provider.
5. **Configuration**: in order to load and use you new stream provider you need to cosnfigure it properly via silo config file. If you ned to use it on the client, you need to add a similar config element to the client config file. It is also possible to configure the stream provider programatically.

``` xml
<OrleansConfiguration xmlns="urn:orleans">
  <Globals>
    <StreamProviders>
      <Provider Type="My.App.MyQueueStreamProvider" Name="MyStreamProvider" GetQueueMessagesTimerPeriod="100ms" AdditionalProperty="MyProperty"/>
    </StreamProviders>
  </Globals>
</OrleansConfiguration>
```
