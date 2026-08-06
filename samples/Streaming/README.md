---
languages:
- csharp
products:
- dotnet
- dotnet-orleans
page_type: sample
name: "Orleans Streaming sample apps"
urlFragment: "orleans-streaming-samples"
description: "A collection of Orleans sample apps demonstrating streaming capabilities."
---

# Orleans Streaming samples

This folder contains two samples demonstrating Orleans Streams.

The first sample, in the `Simple` folder, demonstrates pub/sub using Azure Event Hub and an implicit consumer.

The second sample, in the `CustomDataAdapter` folder, demonstrates how to configure a custom data adapter so that Orleans can consume stream messages which did not originate from Orleans.

## Sample prerequisites

This sample is written in C# and targets .NET 10. It requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later.

## Building the sample

To download and run the sample, follow these steps:

1. Download and unzip the sample.
2. In Visual Studio (2022 or later):
    1. On the menu bar, choose **File** > **Open** > **Project/Solution**.
    2. Navigate to the folder that holds the unzipped sample code, and open the C# project (.csproj) file.
    3. Choose the <kbd>F5</kbd> key to run with debugging, or <kbd>Ctrl</kbd>+<kbd>F5</kbd> keys to run the project without debugging.
3. From the command line:
   1. Navigate to the folder that holds the unzipped sample code.
   2. At the command line, type [`dotnet run`](https://docs.microsoft.com/dotnet/core/tools/dotnet-run).
