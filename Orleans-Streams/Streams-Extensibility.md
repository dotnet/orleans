---
layout: page
title: Orleans Streams Extensibility
---
{% include JB/setup %}

There is a number of ways developers can extend the currently implemented behoivour of Orleans Streaming. We will describe those below, starting from the simplest and following with more complicated cases.

## Stream Provider Configuration

Currently implemented stream providers (Simple Message Stream Provider and Persistent Stream Providers) already support a number of configuration options.

### Simple Message Stream Provider Configuration:

SMS Stream Provider currently supports only a single configuration option:
1) **FireAndForgetDelivery**: this option specifies if the messages sent by SMS stream producer are sent as fire and forget without the way to know if they were delivered or not. When FireAndForgetDelivery is set to false (messages are sent not as FireAndForget), the stream producer's call `stream.OnNext()` returns a Task that represents the processing status of the stream consumer. If this Task suceeds, the producer knows for sure that the message was delivered and processed succesfully. If FireAndForgetDelivery is set to true, the returned Task only expresses that the Orleans runtime has accepted the message and queued it for further delivery. The default value for FireAndForgetDelivery is false. 

### Persistent Stream Provider Configuration:

All persistent stream providers supports the following configuration options:

1) **GetQueueMessagesTimerPeriod** - how much time the pulling agents wait after the last attempt to pull from the queue that did not return any itimes before the agent attemps to pull again. Default is 100 milliseconds.

2) **InitQueueTimeout** - how much time the puling agents waits for the adapter to initialize the connection with the queue. Default is 5 seconds.

3) **QueueBalancerType** - the type of balancing algorithm to be used to balance queues to silos and agents. Default is 5 ConsistentRingBalancer.

### Azure Queue Stream Provider Configuration:

Azure Queue stream provider supports the following configuration options, in addition to what is supported by  Persistent Stream Provider:

1) **DataConnectionString** - the Azure Queue storage connection string.

2) **DeploymentId** - the deployment id of this Orlean cluster (usualy similar to Azure Deployment Id).

3) **CacheSize** - the size of the persistent provider cache that is used to store stream message for further delivery. Default is 4096.
