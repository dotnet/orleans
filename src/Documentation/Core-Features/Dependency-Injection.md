---
layout: page
title: Dependency Injection
---

# What is Dependency Injection

Dependency injection (DI) is a technique for achieving loose coupling between objects and their collaborators, or dependencies.
Rather than directly instantiating collaborators, or using static references, the objects a class needs in order to perform its actions are provided to the class in some fashion. Most often, classes will declare their dependencies via their constructor, allowing them to follow the [Explicit Dependencies Principle](http://deviq.com/explicit-dependencies-principle/). This approach is known as "constructor injection".

# DI in Orleans

Dependency Injection is currently supported on both silo side and client side. 

By integrating Orleans with dependency injection, now it is possible to inject dependencies into application [Grains](../Getting-Started-With-Orleans/Grains.md), providers, and other extension points of orleans. 

Orleans uses the DI abstraction created for [ASP.NET Core](https://docs.asp.net).
For a detailed explanation about how it works, check out the [official documentation](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection).

# Configuring DI
DI for silos and client is configured through `SiloHostBuilder` and `ClientBuilder` respectively.
For example, to configure DI on the silo side with a `IInjectedService` singleton, whose declaration and implementation as below, 

``` csharp
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

you register it through `SiloHostBuilder`:

``` csharp
var siloBuilder = new SiloHostBuilder();
//configure silo DI with a IInjectedService using a Action<IServiceCollection> delegate.
siloBuilder.ConfigureServices(svc=>svc.AddSingleton<IInjectedService,InjectedService>());
```

On the client side, you do the same via `ClientBuilder`:

``` csharp
var clientBuilder = new ClientBuilder();
//configure client DI with a IInjectedService using a Action<IServiceCollection> delegate.
clientBuilder.ConfigureServices(svc=>svc.AddSingleton<IInjectedService,InjectedService>());
```

This example shows how a [`Grain`](../Getting-Started-With-Orleans/Grains.md) can inject `IInjectedService` via constructor injection:

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
```

# Test Framework Integration

DI truly shines when coupled with a testing framework.
You can read about the components for testing in Orleans by following our [tutorials](../../1.5/Tutorials/Unit-Testing-Grains.md).

One popular use of DI is to mock services.
In order to set up mocked service with your test cluster, you need to do two things.
First, you need to implement mocks of your services.
This is done in our example using [Moq](https://github.com/moq/), a popular mocking framework for .NET.
Here is an example of mocking a service and inject the mocked service into DI.


``` csharp
public class MockServices
{
    public static void ConfigureMockServices(IServiceCollection services)
    {
        var mockInjectedService = new Mock<IInjectedService>();

        mockInjectedService.Setup(t => t.GetTicks()).Returns(knownDateTime);
        services.AddSingleton<IInjectedService>(mockInjectedService.Object);
    }
}
```

Now, to include these services in your test cluster, you need to configure mocked services with `TestClusterBuilder`.
Here is an example of how to configure DI with mocked services on both silo and client side.

``` csharp
[TestClass]
public class IInjectedServiceTests
{
    private readonly TestCluster hostedCluster;

    [TestInitialize]
    public void Setup()
    {
        if (testCluster == null)
        {
             var builder = new TestClusterBuilder();
             //use ISiloBuilderConfigurator to configure ISiloHostBuilder, and add the configurator to TestClusterBuilder, 
             //so the configuration will be applied to silos when test cluster build ISiloHostBuilder and starts the silos.
             builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
             //use IClientBuilderConfigurator to configure IClientBuilder, and add the configurator to TestClusterBuilder,
             //so the configuration would be applied when test clusetr builder IClientBuilder start the client.
             builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();

            //builde the test cluster
            var testCluster = builder.Build();
            //deploy test cluster
            testCluster.Deploy();
            this.hostedCluster = testCluster;
        }
    }

    private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
    {
        public void Configure(ISiloHostBuilder hostBuilder)
        {
            //configure silo side DI with mock services
            hostBuilder.ConfigureServices(MockServices.ConfigureMockServices);
        }
    }

    private class MyClientBuilderConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            //configure client side DI with mock services
            clientBuilder.ConfigureServices(MockServices.ConfigureMockServices);
        }
    }
}
```
