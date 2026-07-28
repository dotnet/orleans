---
languages:
- csharp
products:
- dotnet
- dotnet-orleans
page_type: sample
name: "Orleans ASP.NET Core Blazor Server sample"
urlFragment: "orleans-aspnet-core-blazor-server-sample"
description: "An example of an Orleans sample using Blazor and ASP.NET Core."
---

# Orleans ASP.NET Core Blazor Server sample

This sample demonstrates how to integrate [ASP.NET Core Blazor](https://docs.microsoft.com/aspnet/core/blazor/)
with [Microsoft Orleans](https://docs.microsoft.com/dotnet/orleans/).
This demonstrates the in-browser [Blazor Server hosting model](https://docs.microsoft.com/aspnet/core/blazor/hosting-models#blazor-server).

The application leverages Orleans Streams to provide real-time synchronization between browser sessions.

The application is based on the [official tutorial](https://dotnet.microsoft.com/learn/aspnet/blazor-tutorial/intro), adapted to showcase integration with Orleans.

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

## Running the sample

Run the sample by opening a terminal window and executing the following at the command prompt:

```dotnetcli
dotnet run
```

Once the application appears to have started, you can access it using a Web browser by navigating to [http://localhost:5000/](http://localhost:5000/).

Opening multiple browser sessions to that address will showcase real-time synchronization between browser sessions in the *Todo list* demo.

## Demos

Both client-side and server-side apps showcase the same three demos:

### Counter

Shows a button that updates a counter.
This demonstrates basic logic in Blazor.
This demo does not integrate with Orleans.

### Fetch Data

A page that fetches read-only data from Orleans.

The application sources this data from an Orleans grain by making a grain call via the [`WeatherForecastService`](./Services/WeatherForecastService.cs) type.

### Todo list

A page that manages a todo list.
Allows creating, editing and removing todo items.

The application manages this data via direct calls to supporting grains in the Orleans cluster.
The application subscribes to individual changes to this list via Orleans Streams.
This allows it to keep the todo list updated in real-time, upon changes from other browser sessions.
Changes are rendered and sent in real-time to the browser via the underlying SignalR infrastructure in Blazor.

To demonstrate real-time server updates, open multiple browser windows showing the server-side todo demo,
and then proceed to perform changes to the todo list from any window. The other windows will mirror the update in real-time.

![Screenshot](screenshot.png)
