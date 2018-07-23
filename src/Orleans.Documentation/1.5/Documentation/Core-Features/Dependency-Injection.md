---
layout: page
title: Dependency Injection
---

[!include[](../../warning-banner.md)]

# What is Dependency Injection

Dependency injection (DI) is a software design pattern that implements inversion of control for resolving dependencies.

Orleans is using the abstraction written by the developers of [ASP.NET Core](https://docs.asp.net). For a detailed explanation about how it works, check out the [official documentation](https://docs.asp.net/en/latest/fundamentals/dependency-injection.html#dependency-injection).

# DI in Orleans

Dependency Injection is currently supported only on the server side within Orleans.

Orleans makes it possible to inject dependencies into application [Grains](../Getting-Started-With-Orleans/Grains.md).

However Orleans supports every container dependent injection mechanisms, one of the most commonly used method is constructor injection.

Theoretically any type can be injected which was previously registered in a [`IServiceCollection`](https://docs.asp.net/projects/api/en/latest/autoapi/Microsoft/Extensions/DependencyInjection/IServiceCollection/index.html) during Silo startup.
*Note**:
As Orleans is evolving, as of the current plans it will be possible to leverage dependency injection in other application classes as well, like [`StreamProviders`](../Orleans-Streams/Stream-Providers.md). 

# Configuring DI

The DI configuration is a global configuration value and must be configured there.

Orleans is using a similar approach as ASP.NET Core to configure DI. You must have a `Startup` class within your application which must contain a `ConfigureServices` method. It must return an object instance of type: `IServiceProvider`.

Configuration is done by specifying the type of your `Startup` class via one of the methods described below.

**Note**:
Previously DI configuration was specified at the cluster node level, this was changed in the recent release. 

## Configuring from Code

It is possible to tell Orleans what `Startup` type you like to use with code based configuration. There is an extension method named `UseStartup` on the `ClusterConfiguration` class which you can use to do that.

``` csharp
var configuration = new ClusterConfiguration();

configuration.UseStartupType<MyApplication.Configuration.MyStartup>();
``` 

## Configuring via XML

To register your `Startup` class with Orleans you add a `Startup` element to the `Defaults` section and in the `Type` attribute you specify the assembly-qualified name for the type.

``` XML
<?xml version="1.0" encoding="utf-8" ?>
<tns:OrleansConfiguration xmlns:tns="urn:orleans">
  <tns:Defaults>
    <tns:Startup Type="MyApplication.Configuration.Startup,MyApplication" />
  </tns:Defaults>
</tns:OrleansConfiguration>
```
# Example

Here is a complete `Startup` class example:

``` csharp
namespace MyApplication.Configuration
{
    public class MyStartup
    {
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IInjectedService, InjectedService>();

            return services.BuildServiceProvider();
        }
    }
}
```

This example shows how a [`Grain`](../Getting-Started-With-Orleans/Grains.md) can utilize `IInjectedService` via constructor injection and also the complete declaration and implementation of the injected service:

``` csharp
public interface ISimpleDIGrain : IGrainWithIntegerKey
{
    Task<long> GetTicksFromService();
}

public class SimpleDIGrain : Grain, ISimpleDIGrain
{
    private readonly IInjectedService injectedService;

    public SimpleDIGrain(IInjectedService injectedService)
    {
        this.injectedService = injectedService;
    }

    public Task<long> GetTicksFromService()
    {
        return injectedService.GetTicks();
    }
}

public interface IInjectedService
{
    Task<long> GetTicks();
}

public class InjectedService : IInjectedService
{
    public Task<long> GetTicks()
    {
        return Task.FromResult(DateTime.UtcNow.Ticks);
    }
}
```

# Test Framework Integration

DI truly shines when coupled with a testing framework to verify the correctness of the code that build. You can read about the components for testing in Orleans by following our [tutorials](../../Tutorials/Unit-Testing-Grains.md).

You will need to do two things to set up DI with tests. First you will need to implement mocks of your services. This is done in our example using [Moq](https://github.com/moq/), a popular mocking framework for .NET. Here is an example of mocking a service.


``` csharp
public class MockServices
{
    public IServiceProvider ConfigureServices(IServiceCollection services)
    {
        var mockInjectedService = new Mock<IInjectedService>();

        mockInjectedService.Setup(t => t.GetTicks()).Returns(knownDateTime);
        services.AddSingleton<IInjectedService>(mockInjectedService.Object);
        return services.BuildServiceProvider();
    }
}
```

To include these services in your test silo, you will need to specify MockServices as the silo startup class. Here is an example of doing this.

``` csharp
[TestClass]
public class IInjectedServiceTests: TestingSiloHost
{
    private static TestingSiloHost host;

    [TestInitialize]
    public void Setup()
    {
        if (host == null)
        {
            host = new TestingSiloHost(
                new TestingSiloOptions
                {
                    StartSecondary = false,
                    AdjustConfig = clusterConfig =>
                    {
                        clusterConfig.UseStartupType<MockServices>();
                    }
                });
        }
    }
}
```
