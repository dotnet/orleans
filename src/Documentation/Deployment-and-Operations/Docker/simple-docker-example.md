---
layout: page
title: Simple Docker deployment
---

> **Note**: There is still some issue with Orleans in docker, related to the shutdown sequence. Please see the [following issue on GitHub](https://github.com/dotnet/orleans/issues/3620).

# Prerequisites

- Basic knowlegdes on Docker
- The Docker engine installed (this documentation should apply to Windows and Linux containers)
- A recent version of docker-compose installed
- A connection string to an Azure storage account that will be used for silo membership and client gateway provider.

# Getting the code

The source code used in this sample is [available here](https://github.com/dotnet/orleans/tree/master/Samples/Docker-Simple). Download it locally, and create a file named "connection-string.txt" that will contains the connection string to the azure storage that you will use for this sample.

The source code is a pretty standard Orleans application, with the exceptions of  `docker-compose.yaml`, and the `Dockerfile` in the Client and the Silo subfolder. They will be used to build the container images.

The only interesting part is in `Silo\Program.cs`:

``` csharp
AppDomain.CurrentDomain.ProcessExit += (object sender, EventArgs e) =>
{
    Console.WriteLine("ProcessExit fired");
    Task.Run(StopSilo);
};
```

When asked to stop (via the command `docker stop`), the event `AppDomain.CurrentDomain.ProcessExit` will be raised, so you should implement the cleaning/shutdown silo call here.

# Building the application

Executing `Build.cmd` should create two container images: one image for the silo, and one image for the client. Here the build is done in two steps:

## Building the dotnet app

The command `dotnet publish -c Release -o publish` will build the application and all dependencies for the silo and the client, in `Silo\publish` and `Client\publish` respectively.

## Building the docker images

In this example we use `docker-compose` to build the two container images. The content of the `docker-compose.yaml` is pretty simple, and just use a default network overlay to connect the silo and the client .

``` yaml
version: '3'

services:
  client:
    build: Client
  silo:
    build: Silo
```

The `client` and the `silo` image will be build by looking at the content in `Client\Dockerfile` and `Silo\Dockerfile`. The content of these files is pretty straightforward. Here is for example the `Dockerfile` for the client:

``` Dockerfile
FROM microsoft/dotnet:runtime

WORKDIR /app
COPY publish/* ./

ENTRYPOINT ["dotnet", "Client.dll"]
```

# Running the application

You can start the silo with this command, while you are in the solution root folder:

`docker-compose up -d silo`

This will create the network layer and start a silo in the background. You can see the silo's logs with:

`docker-compose logs silo`.

Sample output:

```
silo_1    | warn: Orleans.Runtime.RuntimeStatisticsGroup[100708]
silo_1    |       CPU & Memory perf counters are not available in .NET Standard. Load shedding will not work yet
silo_1    | Silo started
```

Once you see in the logs that the silo is properly started, you can run the client:

`docker-compose run client`

This command will start the client and attach the console, this way you will be able to see the output.

Sample output:

```
Ping: 1      
Ping: 2      
Ping: 3      
Ping: 4      
Ping: 5      
Ping: 6      
Ping: 7      
Ping: 8      
Ping: 9      
Ping: 10
```

To stop and delete everything, just use:

`docker-compose down`