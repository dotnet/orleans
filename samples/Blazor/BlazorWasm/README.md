---
languages:
- csharp
products:
- dotnet
- dotnet-orleans
page_type: sample
name: "Orleans ASP.NET Core Blazor WebAssembly sample"
urlFragment: "orleans-aspnet-core-blazor-wasm-sample"
description: "An example of an Orleans sample using Blazor and ASP.NET Core."
---

# Orleans ASP.NET Core Blazor WebAssembly sample

This sample demonstrates how to integrate [ASP.NET Core Blazor](https://docs.microsoft.com/aspnet/core/blazor/)
with [Microsoft Orleans](https://docs.microsoft.com/dotnet/orleans/). This demonstrates the in-browser [Blazor WebAssembly hosting model](https://docs.microsoft.com/aspnet/core/blazor/hosting-models#blazor-webassembly).

The client-side sample application leverages ASP.NET Web API running alongside Orleans for standard web communication.

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

Run the sample by opening two terminal windows and executing the following in one terminal:

```dotnetcli
dotnet run --project BlazorWasm.Server
```

Execute the following in the other terminal:

```dotnetcli
dotnet run --project BlazorWasm.Client
```

Once both applications appear to have started, you can access them at these addresses:

* The Swagger UI, hosted by the BlazorWasm.Server process: [http://localhost:5000/swagger/index.html](http://localhost:5000/swagger/index.html)
* The Client-Side Blazor App hosted by the BlazorWasm.Client process: [http://localhost:62654/](http://localhost:62654/)

## Demos

The application showcases three demos:

### Counter

Shows a button that updates a counter.
This demonstrates basic logic in Blazor.
This demo does not integrate with Orleans.

### Fetch Data

A page that fetches read-only data from Orleans.
The client-side app sources this data from the ASP.NET Core Web API running alongside Orleans.

### Todo

A page that manages a todo list.
Allows creating, editing and removing todo items.

The client-side app manages this data via REST calls to the ASP.NET Core Web API running alongside Orleans.
