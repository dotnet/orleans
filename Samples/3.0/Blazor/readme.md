## Orleans ASP.NET Core Blazor Sample

Orleans ASP.NET Core Blazor Sample targetting:

* .NET Core 3.1
* Orleans 3.0

This sample demonstrates how to integrate [ASP.NET Core Blazor](https://docs.microsoft.com/en-us/aspnet/core/blazor/?view=aspnetcore-3.1)
with [Microsoft Orleans](https://dotnet.github.io/orleans/). This demonstrates both client-side and server-side Blazor hosting models.

The server-side sample application leverages Orleans Streams to provide real-time synchronization between browser sessions.

The client-side sample application leverages ASP.NET Web API running alongside Orleans for standard web communication.

Both applications are based on the [official tutorial](https://docs.microsoft.com/en-us/aspnet/core/tutorials/build-your-first-blazor-app?view=aspnetcore-3.1), adapted to showcase integration with Orleans.

### Prerequisites

* Visual Studio 2019 v.16.4+
* .NET Core 3.1

### How To Run

To run this sample, open the solution in Visual Studio 2019 with the prerequisites above installed, and set solution startup to run the projects below:

* `Sample.Silo`
* `Sample.ServerSide`
* `Sample.ClientSide`

Upon solution startup, ensure that you can access these addresses:

* The Orleans Dashboard @ [http://localhost:8080/](http://localhost:8080/)
* The Swagger UI @ [http://localhost:8081/swagger/index.html](http://localhost:8081/swagger/index.html)
* The Client-Side Blazor App @ [http://localhost:62653/](http://localhost:62653/)
* The Server-Side Blazor App @ [https://localhost:44344/](https://localhost:44344/)

Opening multiple browser sessions of the server-side blazor app will showcase real-time synchronization between browser sessions in the *Todo* demo.

### Demos

Both client-side and server-side apps showcase the same three demos:

#### Counter

Shows a button that updates a counter.
 This demonstrates basic logic in Blazor. This demo does not integrate with Orleans.

#### Fetch Data

A page that fetches read-only data from Orleans.

* The client-side app sources this data from the ASP.NET Core Web API running alongside Orleans.
* The server-side app sources this data from the Orleans cluster itself via the Orleans Cluster Client running in the application server process.

#### Todo

A page that manages a todo list.
Allows creating, editing and removing todo items.

* The client-side app manages this data via REST calls to the ASP.NET Core Web API running alongside Orleans.
  The client-side app does not support real-time server updates at this time.
* The server-side app manages this data via direct calls to supporting grains in the Orleans cluster, via the Orleans Cluster Client.
  The server-side app also subscribes to individual changes to this list via Orleans Streams.
  This allows it to keep the todo list updated in real-time, upon changes from other browser sessions.
  Changes are rendered and sent in real-time to the browser via the underlying SignalR infrastructure in Blazor.
 
To demonstrate real-time server updates, open multiple browser windows showing the server-side todo demo,
and then proceed to perform changes to the todo list from any window. The other windows will mirror the update in real-time.

If running both the client-side and server-side applications at the same time,
the server-side application will also react to updates from the client-side application, as the underlying grains are the same.
However, the client-side application will not react to notifications at this time.