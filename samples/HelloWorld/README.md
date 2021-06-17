# *Hello, World!* Sample Application

*Hello, World!* applications are a rite of passage for programmers and this is our *Hello, World!* sample for Orleans.
The sample consists of a single project which starts the Orleans-based application, sends a message to a grain, prints the response, and terminates when the user presses a key.

### Demonstrates

* How to get started with Orleans
* How to define and implement grain interface
* How to get a reference to a grain and call a grain

To start our tour of the application, open [`IHelloGrain.cs`](./IHelloGrain.cs) and you will find the following interface declaration:

``` C#
public interface IHelloGrain : IGrainWithStringKey
{
    Task<string> SayHello(string greeting);
}
```

This defines the `IHelloGrain` grain interface.
We know it's a grain interface because it implements `IGrainWithStringKey`.
This means that when we want to get a reference to a grain implementing `IHelloGrain`, we will identify the grain instance using an string value.
In our case, as you will see later in [`Program.cs`](./Program.cs), we will use the string `"friend"` to identify the grain we wish to communicate with, but this could be any string:

``` C#
var friend = grainFactory.GetGrain<IHelloGrain>("friend");
```

Now, open [`HelloGrain.cs`](./HelloGrain.cs) and we will see the implementation of our `IHelloGrain` interface:

``` C#
public class HelloGrain : Grain, IHelloGrain
{
    public Task<string> SayHello(string greeting) => Task.FromResult($"Hello, {greeting}!");
}
```

We know that `HelloGrain` is a grain implementation because it inherits from the `Grain` base type.
That type is used to identify the grain classes in an application.

`HelloGrain` implements `IHelloGrain` by returning a simple string: `$"Hello, {greeting}"`.

Open [`Program.cs`](./Program.cs) to see how Orleans is configured:

``` C#
using var host = new HostBuilder()
    .UseOrleans(builder =>
    {
        builder.UseLocalhostClustering();
    })
    .Build();

await host.StartAsync();

// Get the grain factory
var grainFactory = host.Services.GetRequiredService<IGrainFactory>();

// Get a reference to the HelloGrain grain with the key "friend".
var friend = grainFactory.GetGrain<IHelloGrain>("friend");

// Call the grain and print the result to the console
var result = await friend.SayHello("Good morning!"); 
Console.WriteLine("\n\n{0}\n\n", result);

Console.WriteLine("Orleans is running.\nPress Enter to terminate...");
Console.ReadLine();
Console.WriteLine("Orleans is stopping...");

await host.StopAsync();
```

This program creates a new [`HostBuilder`](https://docs.microsoft.com/dotnet/core/extensions/generic-host) and adds Orleans to it by calling the `UseOrleans` extension method.
Within that call, it configures Orleans to use localhost clustering, which is used for development and testing scenarios.
The program then starts the host and retrieves the `IGrainFactory` instance from its service provider.
Using `IGrainFactory`, we can get a *reference* to a grain.
In this case, we want a reference to the `HelloGrain` instance named `"friend"`, so we call `grainFactory.GetGrain<IHelloGrain>("friend")`.
Once we have a reference, we can put it to use and call `friend.SayHello("Good morning!")`, printing the result to the console.

Now it's time to run the sample. Do so by executing the following command in a terminal window:

``` powershell
dotnet run
```

You should see `Hello, Good morning!!` printed to the console.

Orleans instantiated the `"friend"` instance of `HelloGrain` for us automatically when we first made a call to it (`friend.SayHello(...)`).
As developers, we do not need to manage the lifetimes of these grains: Orleans activates them as needed and it deactivates them when they become idle.