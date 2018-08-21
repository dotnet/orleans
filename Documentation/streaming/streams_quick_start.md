---
layout: page
title: Orleans Streams Quick Start
---

# Orleans Streams Quick Start

This guide will show you a quick way to setup and use Orleans Streams.
To learn more about the details of the streaming features, read other parts of this documentation.

## Required Configurations

In this guide we'll use a Simple Message based Stream which uses grain messaging to send stream data to subscribers. We will use the in-memory storage provider to store lists of subscriptions so it is not a wise choice for real production applications.

On silo, where hostBuilder is an ISiloHostBuilder

``` csharp
hostBuilder.AddSimpleMessageStreamProvider("SMSProvider")
           .AddMemoryGrainStorage("PubSubStore");
```

On cluster client, where clientBuilder is an IClientBuilder

``` csharp
clientBuilder.AddSimpleMessageStreamProvider("SMSProvider");
```

Now we can create streams, send data using them as producers and also receive data as subscribers.

## Producing Events

Producing events for streams is relatively easy. You should first get access to the stream provider which you defined in the config above (`SMSProvider`) and then choose a stream and push data to it.

``` csharp
//Pick a guid for a chat room grain and chat room stream
var guid = some guid identifying the chat room
//Get one of the providers which we defined in config
var streamProvider = GetStreamProvider("SMSProvider");
//Get the reference to a stream
var stream = streamProvider.GetStream<int>(guid, "RANDOMDATA");
```

As you can see our stream has a GUID and a namespace. This will make it easy to identify unique streams. For example, in a chat room namespace can "Rooms" and GUID be the owning RoomGrain's GUID.

Here we use the GUID of some known chat room. Now using the `OnNext` method of the stream we can push data to it. Let's do it inside a timer and using random numbers. You could use any other data type for the stream as well.

``` csharp
RegisterTimer(s =>
        {
            return stream.OnNextAsync(new System.Random().Next());
        }, null, TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1000));
```

## Subscribing and receiving streaming data

For receiving data we can use implicit/explicit subscriptions, which are fully described in other pages of the manual. Here we use implicit subscriptions which are easier. When a grain type wants to implicitly subscribe to a stream it uses the attribute `ImplicitStreamSubscription (namespace)]`.

For our case we'll define a ReceiverGrain like this:

``` csharp
[ImplicitStreamSubscription("RANDOMDATA")]
public class ReceiverGrain : Grain, IRandomReceiver
```

Now whenever some data is pushed to the streams of namespace RANDOMDATA as we have in the timer, a grain of type `ReceiverGrain` with the same guid of the stream will receive the message. Even if no activations of the grain currently exist, the runtime will automatically create a new one and send the message to it.

In order for this to work however, we need to complete the subscription process by setting our `OnNext` method for receiving data. So our `ReceiverGrain` should call in its `OnActivateAsync` something like this

``` csharp
//Create a GUID based on our GUID as a grain
var guid = this.GetPrimaryKey();
//Get one of the providers which we defined in config
var streamProvider = GetStreamProvider("SMSProvider");
//Get the reference to a stream
var stream = streamProvider.GetStream<int>(guid, "RANDOMDATA");
//Set our OnNext method to the lambda which simply prints the data, this doesn't make new subscriptions
await stream.SubscribeAsync<int>(async (data, token) => Console.WriteLine(data));
```

We are all set now. The only requirement is that something triggers our producer grain's creation and then it will registers the timer and starts sending random ints to all interested parties.

Again, this guide skips lots of details and is only good for showing the big picture. Read other parts of this manual and other resources on RX to gain a good understanding on what is available and how.

Reactive programming can be a very powerful approach to solve many problems. You could for example use LINQ in the subscriber to filter numbers and do all sorts of interesting stuff.


## Next
[Orleans Streams Programming APIs](streams_programming_APIs.md)
