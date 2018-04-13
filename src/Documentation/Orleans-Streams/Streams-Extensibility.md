---
layout: page
title: Orleans Streams Extensibility
---

# Orleans Streams Extensibility

There are three ways developers can extend the currently implemented behaviour of Orleans Streaming:

- Utilize or extend Stream Provider Configurators.
- Write a Custom Queue Adapter.
- Write a New Stream Provider

We will describe those below. Please read the [Orleans Streams Implementation](Streams-Implementation.md) before reading this section to have a high level view of the internal implementation.

## Stream Provider Configuration

Currently implemented stream providers support a number of configuration options.

### Simple Message Stream Provider Configuration. 
SMS Stream Provider is configured through following options, using `AddSimpleMessageStreamProvider` extension method on `ISiloHostBuilder` or `IClientBuilder`: 

```csharp
public class SimpleMessageStreamProviderOptions
{
    public bool FireAndForgetDelivery { get; set; } = DEFAULT_VALUE_FIRE_AND_FORGET_DELIVERY;
    public const bool DEFAULT_VALUE_FIRE_AND_FORGET_DELIVERY = false;

    public bool OptimizeForImmutableData { get; set; } = DEFAULT_VALUE_OPTIMIZE_FOR_IMMUTABLE_DATA;
    public const bool DEFAULT_VALUE_OPTIMIZE_FOR_IMMUTABLE_DATA = true;

    public StreamPubSubType PubSubType { get; set; } = DEFAULT_PUBSUB_TYPE;
    public static StreamPubSubType DEFAULT_PUBSUB_TYPE = StreamPubSubType.ExplicitGrainBasedAndImplicit;
}
```

### Persistent Stream Provider Configuration. 
All persistent stream providers are configured through `ISiloPersistentStreamConfigurator` implementations on silo side, or `IClusterClientPersistentStreamConfigurator` implementations on client side. Different persistent stream provider have different components and options to configure, hence configured through their specific implementation of persistent stream configurator interface. But minimumly, all persistent stream provider supports configuring following components through following methods

- **ConfigureStreamPubSub** method - this method configures stream pubsub to use, supported type: 

``` csharp
public enum StreamPubSubType
{
    ExplicitGrainBasedAndImplicit,
    ExplicitGrainBasedOnly,
    ImplicitOnly,
}
```
- **ConfigurePullingAgent** method - this method configures pulling agent. It is only available on `ISiloPersistentStreamConfigurator`, since no need for client to configure pulling agent. It is configured through options below:

``` csharp
public class StreamPullingAgentOptions
{
    //how much time the pulling agents wait after the last attempt to pull from the queue that did not return any items before the agent attempts to pull again. Default is 100 milliseconds.
    public TimeSpan GetQueueMsgsTimerPeriod { get; set; } = DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD;
    public static readonly TimeSpan DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD = TimeSpan.FromMilliseconds(100);

    //how much time the pulling agents waits for the adapter to initialize the connection with the queue. Default is 5 seconds.
    public TimeSpan InitQueueTimeout { get; set; } = DEFAULT_INIT_QUEUE_TIMEOUT;
    public static readonly TimeSpan DEFAULT_INIT_QUEUE_TIMEOUT = TimeSpan.FromSeconds(5);

    public TimeSpan MaxEventDeliveryTime { get; set; } = DEFAULT_MAX_EVENT_DELIVERY_TIME;
    public static readonly TimeSpan DEFAULT_MAX_EVENT_DELIVERY_TIME = TimeSpan.FromMinutes(1);

    public TimeSpan StreamInactivityPeriod { get; set; } = DEFAULT_STREAM_INACTIVITY_PERIOD;
    public static readonly TimeSpan DEFAULT_STREAM_INACTIVITY_PERIOD = TimeSpan.FromMinutes(30);
}
```
- **ConfigureLifecycle** method - this method configures in which silo/client lifecycle stage the stream provider would initialize and start. It is configured through options below: 

``` csharp
public class StreamLifecycleOptions
{
    [Serializable]
    public enum RunState
    {
        None,
        Initialized,
        AgentsStarted,
        AgentsStopped,
    }

    public RunState StartupState = DEFAULT_STARTUP_STATE;
    public const RunState DEFAULT_STARTUP_STATE = RunState.AgentsStarted;

    public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
    public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

    public int StartStage { get; set; } = DEFAULT_START_STAGE;
    public const int DEFAULT_START_STAGE = ServiceLifecycleStage.Active;
}

```
- **ConfigurePartitionBalancing** method - this method configures which `IStreamQueueBalancer` to use. You can configure any queue balancer we support natively or custom ones you developped. This method is not available for `IClusterClientPersistentStreamConfigurator`.

### Azure Queue Stream Provider Configuration
Azure queue stream provider is configured through `SiloAzureQueueStreamConfigurator`, which implements`ISiloPersistentStreamConfigurator`, on the silo side, and configured through `ClusterClientAzureQueueStreamConfigurator`, which implements `IClusterClientPersistentStreamConfigurator`, on the client side. Since Azure queue stream provider is a persistent stream provider, so it supports all the configuring method mentioned on **Persistent Stream Provider Configuration** section above. In addition to that , it supports configuring Azure queue stream provider specific components through following method: 

- **ConfigureAzureQueue** - this method configures Azure Queue specific settings. It is configured through options below:

```csharp
public class AzureQueueOptions
{
    [RedactConnectionString]
    public string ConnectionString { get; set; }

    public TimeSpan? MessageVisibilityTimeout { get; set; }   
}
```
- **ConfigureCache** - this method configures cache size. Only available on `SiloAzureQueueStreamConfigurator`, not available on `ClusterClientAzureQueueStreamConfigurator`.
- **ConfigurePartitioning** - this method configures queue counts to use. 

It would be totally possible and a lot of times easy to provide additional configuration options. For example, in some scenarios developers might want more control over queue names used by the Queue Adapter. This is currently abstracted away with [`IStreamQueueMapper`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IStreamQueueMapper.cs), but there is currently no way to configure which `IStreamQueueMapper` to use without writing a new code. We would be happy to provide such an option, if needed. So please consider adding more configuration options to existing stream providers before writing a completely new  provider.


## Writing a Custom Queue Adapter

If you want to use a different queueing technology, you need to write a queue adapter that abstracts away the access to that queue. Below we provide details on how this should be done. Please refer to [`AzureQueueAdapterFactory`](https://github.com/dotnet/orleans/blob/master/src/OrleansProviders/Streams/AzureQueue/AzureQueueAdapterFactory.cs) for an example.

- Start by defining a `MyQueueFactory` class that implements [**`IQueueAdapterFactory`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IQueueAdapterFactory.cs). You need to:

     a. Initialize the factory: read the passed config values, potentially allocate some data structures if you need to, etc.

     b. Implement a method that returns your `IQueueAdapter`.

     c. Implement a method that returns `IQueueAdapterCache`. Theoretically, you can build your own `IQueueAdapterCache`, but you don't have to. It is a good idea just to allocate and return an Orleans `SimpleQueueAdapterCache`.

     d. Implement a method that returns `IStreamQueueMapper`. Again, it is theoretically possible to build your own `IStreamQueueMapper`, but you don't have to. It is a good idea just to allocate and return an Orleans `HashRingBasedStreamQueueMapper`.

     e. Implement a static factory method which takes `IServiceProvider` and streamProviderName string as input parameters, returns a `MyQueueFactory`. Its signature should look like `public static MyQueueFactory Create(IServiceProvider services, string name)`. And it will be later used in Configuration as the factory delegate which streaming runtime will be using to create `MyQueueFactory`. 

- Implement `MyQueueAdapter` class that implements the [**`IQueueAdapter`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IQueueAdapter.cs) interface, which is an interfaces that manages access to a **sharded queue**. `IQueueAdapter` manages access to a set of queues/queue partitions (those are the queues that were returned by `IStreamQueueMapper`). It provides an ability to enqueue a message in a specified the queue and create an `IQueueAdapterReceiver` for a particular queue.

- Implement `MyQueueAdapterReceiver` class that implements the [**`IQueueAdapterReceiver`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/QueueAdapters/IQueueAdapterReceiver.cs), which is an interfaces that manages access to **one queue (one queue partition)**. In addition to initialization and shutdown, it basically provides one method: retrieve up to maxCount messages from the queue.

- **Configuration**: in order to load and use you new stream provider you need to configure it properly via `ISiloHostBuilder`. If you need to use it on the client, you need to configure it similarly with `IClientBuilder`. Below is an example of configuring using `ISiloHostBuilder`:

``` csharp
var siloHost = new SiloHostBuilder()
                        .AddPersistentStreams("MyStreamProvider", MyQueueFactory.Create, streamBuilder=>streamBuilder
                        .Configure<StreamPullingAgentOptions>(ob => ob.Configure(options => options.GetQueueMessagesTimerPeriod = TimeSpan.FromMilliseconds(100)))
                        .Build();
```

Similarly when configure with `IClientBuilder`: 

``` csharp
var client = new ClientBuilder()
                        .AddPersistentStreams("MyStreamProvider", MyQueueFactory.Create, streamBuilder=>streamBuilder
                        .Configure<StreamPullingAgentOptions>(ob => ob.Configure(options => options.GetQueueMessagesTimerPeriod = TimeSpan.FromMilliseconds(100)))
                        .Build();
```

## Writing a Completely New Stream Provider

It is also possible to write a completely new Stream Provider. In such a case there is very little integration that needs to be done from Orleans perspective. You just need to implement the [`IStreamProviderImpl`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Streams/Providers/IStreamProviderImpl.cs) interface, which is a thin interface that allows application code to get a handle to the stream. Beyond that, it is totally up to you how to implement it. Implementing a completely new Stream Provider might turn to be a rather complicated task, since you might need access to various internal runtime components, some of which may have internal access.

We currently do not envision scenarios where one would need to implement a completely new Stream Provider and could not instead achieve his goals through the two options outlined above: either via extended configuration or by writing a Queue Adapter. However, if you think you have such a scenario, we would like to hear about it and work together on simplifying writing new Stream Providers.
