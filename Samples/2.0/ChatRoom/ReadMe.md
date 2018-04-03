# Chat Room Sample OVerview

This is a simple sample using [Orleans Streaming feature](http://dotnet.github.io/orleans/Documentation/Orleans-Streams/index.html) to build a simple chatroom application. In this application, each client can 
- set its user name for ther chatroom application
- join a channel by channel name
- send messages to the channel
- query history messages in the channel
- query members in the channel
- leave the channel

Each client will
- receive messages sent to the channel by other clients and itself after joined a channel
- stop receive messages after leaved the channel

For the purpose to make this sample simple, one client can only join one channel. It cannot join multiple channels at the same time. So to join another channel, the client need to leave the current channel first.

## Running the sample
From Visual Studio, you can start the OrleansServer project first, wait for it to stablize, and then start multiple OrleansClient projects simultaneously (you can start the project by right click the project -> Debug -> Start new instance).

Alternatively, you can run from the command line:

To start the silo
```
OrleansServer\bin\Debug(Release)\net462\OrleansServer.exe
```


To start the client (you will have to use a different command window)
```
OrleansClient\bin\Debug(Release)\net462\OrleansClient.exe
```
If you build the sample app in debug mode, the exe binary will be in Debug folder. If you build it in release mode, then the exe binary will be in Release folder.

After the client started up, you will see instructions printed on the console, which tells you how to interact with a channel.

## Reference
This sample is based on [a sample writted by @centur](https://github.com/centur/altnet-streams-demo), but revised with changes.