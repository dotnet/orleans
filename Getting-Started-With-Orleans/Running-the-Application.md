---
layout: page
title: Running the Application
---
{% include JB/setup %}

## Configuring Connections to Orleans

To allow applications to communicate with grains from outside Orleans, the framework includes a client library. This client library might be used by a desktop or mobile application, or by a frontend server that renders interactive web pages or exposes a web services API. The client library provides a subset of the Orleans programming model for writing asynchronous clients that can find, create, and communicate with Orleans grains. This requires a few simple steps:

1. Connect to an Orleans gateway 
2. Find existing grains or create new ones 
3. Send messages to grains and receive responses 
4. Receive asynchronous notifications from grains via observers 

## Connecting to a Gateway

To establish a connection, a client calls OrleansClient.Initialize(). This will connect to the gateway silo at the IP address and port specified in the ClientConfiguration.xml file. This file must be placed in the same directory as the Orleans.dll library used by the client. As an alternative, a configuration object can be passed to OrleansClient.Initialize programmatically instead of loading it from a file.

## Configuring the Client

In ClientConfiguration.xml, the Gateway element specifies the address and port of the gateway endpoint that need to match those in OrleansConfiguration.xml on the silo side:

    <ClientConfiguration xmlns="urn:orleans">
       <Gateway Address="<IP address or host name of silo>" Port="30000" />
    </ClientConfiguration>

If an Orleans-based application runs in Windows Azure, the client automatically discovers silo gateways and shouldn't be statically configured. Refer to the Azure application sample for an example of how to configure the client.

## Configuring Silos

In OrleansConfiguration.xml, the ProxyingGateway element specifies the gateway endpoint of the silo, which is separate from the inter-silo endpoint defined by the Networking element and must have a different port number:

    <?xml version="1.0" encoding="utf-8"?>
    <OrleansConfiguration xmlns="urn:orleans">
      <Defaults>
        <Networking Address="" Port="11111" />
        <ProxyingGateway Address="" Port="30000" />
      </Defaults>
    </OrleansConfiguration>
