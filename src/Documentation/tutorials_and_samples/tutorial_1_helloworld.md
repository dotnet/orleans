---
layout: page
title: Tutorial 1 Hello World
---

# Tutorial 1: Hello World

This process recreates the same Hello World sample application available [here](https://github.com/dotnet/orleans/tree/master/Samples/2.0/HelloWorld).

## Overview of the parts

This application consists of a solution that contains four projects: SiloHost, OrleansClient, HelloWorld.Interfaces, and HelloWorld.Grains.

```csharp
[...]
        private static async Task<ISiloHost> StartSilo()
        { 
            // define the cluster configuration 
            var builder = new SiloHostBuilder()
```

Grains are the building blocks of an Orleans application, and you can read more about them in the [Core Concepts section of the Orleans documentation.](http://dotnet.github.io/orleans/Documentation/core_concepts/index.html)

This is the main body of code for the Hello World grain:

```csharp
[...]
namespace HelloWorld.Grains
{
    public class HelloGrain : Orleans.Grain, IHello
    {
        private readonly ILogger logger;
        public HelloGrain(ILogger<HelloGrain> logger)
        {
            this.logger = logger;
        }  

        Task<string> IHello.SayHello(string greeting)
        {
            logger.LogInformation($"SayHello message received: greeting = '{greeting}'");
            return Task.FromResult($"You said: '{greeting}', I say: Hello!");
        }
    }
}

```

A grain class implements one or more grain interfaces, as you can read [here, in the Grains section.](http://dotnet.github.io/orleans/Documentation/grains/index.html))

```csharp
[...]
namespace HelloWorld.Interfaces
{
    public interface IHello : Orleans.IGrainWithIntegerKey
    {
        Task<string> SayHello(string greeting);
    }
}

```

Create the SiloHost and OrleansClient projects as .NET Core App files and the HelloWorld.Interfaces and HelloWorld.Grains projects as .NET Standard Libraries.
Orleans comes into the picture when the NuGet packages are added to each project.

## How the parts work together

SiloHost is started first.
Then, OrleansClient is started.
OrleansClient creates a reference to the IHello grain and calls its SayHello() method through its interface, IHello.
This programming model is built as part of our core concept of distributed Object Oriented Programming.
Next, the grain is activated in the silo.
OrleansClient sends a greeting to the activated grain.
The grain returns a response to OrleansClient, which OrleansClient displays on the console.

## Create the project structure

1. In Visual Studio, create a new Visual C# Console App (.NET Core) project.
2. Name the project **SiloHost** and name the solution **HelloWorld**.
3. Add a second .NET Core Console App project to the HelloWorld solution and name it **OrleansClient**.
4. Add another new project, but this time choose .NET Standard – Class Library. Name it **HelloWorld.Interfaces**.
5. Add a new interface class file to the GrainInterfaces folder and name it **IHello**.
  *Note: If  Visual Studio adds a default class named `Class1.cs`, feel free to delete this file to keep things tidy.
6. For the fourth and final project, add another .NET Standard – Class Library project and name it **HelloWorld.Grains**.
7. Inside the Grains project, add a class and name it **HelloGrain.cs**.
8. Add the Orleans NuGet packages
