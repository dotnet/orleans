---
layout: page
title: Unit Testing
---

# Unit Testing

This tutorial shows how to unit test your grains to make sure they behave correctly.
There are two main ways to unit test your grains, and the method you choose will depend on the type of functionality you are testing.
The `Microsoft.Orleans.TestingHost` NuGet package can be used to create test silos for your grains, or you can use a mocking framework like [Moq](https://github.com/moq/moq) to mock parts of the Orleans runtime that your grain interacts with.

## Using TestCluster

The `Microsoft.Orleans.TestingHost` NuGet package contains `TestCluster` which can be used to create an in-memory cluster, comprised of two silos by default, which can be used to test grains.

```csharp
using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.TestingHost;
using Xunit;

namespace Tests
{
    public class HelloGrainTests
    {
        [Fact]
        public async Task SaysHelloCorrectly()
        {
            var cluster = new TestCluster();
            cluster.Deploy();

            var hello = cluster.GrainFactory.GetGrain<IHelloGrain>(Guid.NewGuid());
            var greeting = await hello.SayHello();

            cluster.StopAllSilos();

            Assert.Equal("Hello, World", greeting);
        }
    }
}
```

Due to the overhead of starting an in-memory cluster you may wish to create a `TestCluster` and reuse it among multiple test cases.
For example this can be done using xUnit's class or collection fixtures (see [https://xunit.github.io/docs/shared-context.html](https://xunit.github.io/docs/shared-context.html) for more details).

In order to share a `TestCluster` between multiple test cases, first create a fixture type:

```csharp
public class ClusterFixture : IDisposable
{
    public ClusterFixture()
    {
        this.Cluster = new TestCluster();
        this.Cluster.Deploy();
    }

    public void Dispose()
    {
        this.Cluster.StopAllSilos();
    }

    public TestCluster Cluster { get; private set; }
}
```

Next create a collection fixture:

```csharp
[CollectionDefinition(ClusterCollection.Name)]
public class ClusterCollection : ICollectionFixture<ClusterFixture>
{
    public const string Name = "ClusterCollection";
}
```

You can now reuse a `TestCluster` in your test cases:

```csharp
using System;
using System.Threading.Tasks;
using Orleans;
using Xunit;

namespace Tests
{
    [Collection(ClusterCollection.Name)]
    public class HelloGrainTests
    {
        private readonly TestCluster _cluster;

        public HelloGrainTests(ClusterFixture fixture)
        {
            _cluster = fixture.Cluster;
        }

        [Fact]
        public async Task SaysHelloCorrectly()
        {
            var hello = _cluster.GrainFactory.GetGrain<IHelloGrain>(Guid.NewGuid());
            var greeting = await hello.SayHell();

            Assert.Equal("Hello, World", greeting);
        }
    }
}
```

xUnit will call the `Dispose` method of the `ClusterFixture` type when all tests have been completed and the in-memory cluster silos will be stopped.
`TestCluster` also has a constructor which accepts `TestClusterOptions` that can be used to configure the silos in the cluster.

If you are using Dependency Injection in your Silo to make services available to Grains, you can use this pattern as well:

```csharp
public class ClusterFixture : IDisposable
{
    public ClusterFixture()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurations>();
        this.Cluster = builder.Build();
        this.Cluster.Deploy();
    }

    public void Dispose()
    {
        this.Cluster.StopAllSilos();
    }

    public TestCluster Cluster { get; private set; }
}

public class TestSiloConfigurations : ISiloBuilderConfigurator {
    public void Configure(ISiloHostBuilder hostBuilder) {
        hostBuilder.ConfigureServices(services => {
            services.AddSingleton<T, Impl>(...);
        });
    }
}
```

## Using Mocks

Orleans also makes it possible to mock many parts of system, and for many of scenarios this is the easiest way to unit test grains.
This approach does have limitations (e.g. around scheduling reentrancy and serialization), and may require that grains include code used only by your unit tests.
The [Orleans TestKit](https://github.com/OrleansContrib/OrleansTestKit) provides an alternative approach which side-steps many of these limitations.

For example, let us imagine that the grain we are testing interacts with other grains.
In order to be able to mock those other grains we also need to mock the `GrainFactory` member of the grain under test.
By default `GrainFactory` is a normal `protected` property, but most mocking frameworks require properties to be `public` and `virtual` to be able to mock them.
So the first thing we need to do is make `GrainFactory` both `public` and `virtual` property:

```csharp
public new virtual IGrainFactory GrainFactory
{
    get { return base.GrainFactory; }
}
```

Now we can create our grain outside of the Orleans runtime and use mocking to control the behaviour of `GrainFactory`:

```csharp
using System;
using System.Threading.Tasks;
using Orleans;
using Xunit;
using Moq;

namespace Tests
{
    public class WorkerGrainTests
    {
        [Fact]
        public async Task RecordsMessageInJournal()
        {
            var data = "Hello, World";

            var journal = new Mock<IJournalGrain>();

            var worker = new Mock<WorkerGrain>();
            worker
                .Setup(x => x.GrainFactory.GetGrain<IJournalGrain>(It.IsAny<Guid>()))
                .Returns(journal.Object);

            await worker.DoWork(data)

            journal.Verify(x => x.Record(data), Times.Once());
        }
    }
}
```

Here we create our grain under test, `WorkerGrain`, using Moq which means we can then override the behaviour of the `GrainFactory` so that it returns a mocked `IJournalGrain`.
We can then verify that our `WorkerGrain` interacts with the `IJournalGrain` as we expect.
