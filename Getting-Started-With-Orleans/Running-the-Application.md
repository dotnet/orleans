---
layout: page
title: Running the Application
---
{% include JB/setup %}

## Configuring Connections to Orleans

To allow applications to communicate with grains from outside Orleans, the framework includes a client library.
This client library might be used by a desktop or mobile application, or by a front end server that renders interactive web pages or exposes a web services API.
The client library provides an API for writing asynchronous clients that communicate with Orleans grains.
Once the client library is connected to an Orleans gateway, a client can send messages to grains, receive responses and receive asynchronous notifications from grains via observers.

## Connecting to a Gateway

To establish a connection, a client calls `GrainClient.Initialize()`.
This will connect to the gateway silo at the IP address and port specified in the _ClientConfiguration.xml_ file.
This file must be placed in the same directory as the _Orleans.dll_ library used by the client.
As an alternative, a configuration object can be passed to `GrainClient.Initialize()` programmatically instead of loading it from a file.

## Configuring the Client

In _ClientConfiguration.xml_, the `Gateway` element specifies the address and port of the gateway endpoint that need to match those in _OrleansConfiguration.xml_ on the silo side:

```xml
<ClientConfiguration xmlns="urn:orleans">
    <Gateway Address="<IP address or host name of silo>" Port="30000" />
</ClientConfiguration>
```

If an Orleans-based application runs in Windows Azure, the client automatically discovers silo gateways and shouldn't be statically configured.
Refer to the [Azure application sample](../Samples-Overview/Azure-Web-Sample) for an example of how to configure the client.

## Configuring Silos

In _OrleansConfiguration.xml_, the `ProxyingGateway` element specifies the gateway endpoint of the silo, which is separate from the inter-silo endpoint defined by the Networking element and must have a different port number:

```xml
<?xml version="1.0" encoding="utf-8"?>
<OrleansConfiguration xmlns="urn:orleans">
    <Defaults>
    <Networking Address="" Port="11111" />
    <ProxyingGateway Address="" Port="30000" />
    </Defaults>
</OrleansConfiguration>
```

## Next
Back to the [Orleans documentation](../) index

Back to the [Getting Started](./) index

Forward to the [Step-by-step Tutorials](../Step-by-step-Tutorials)
