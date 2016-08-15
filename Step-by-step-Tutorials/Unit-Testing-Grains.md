---
layout: page
title: Unit Testing Grains
---

This tutorial shows you how to write unit tests for your grains to make sure they are behaving correctly.
For a distributed application written in Orleans, you'll need load tests and integration tests as well but here we'll only focus on unit tests.

Orleans makes it possible to mock many of its parts ([example](https://github.com/dotnet/orleans/tree/master/Samples/UnitTesting.Minimal)) but here we only focus on simply running grains in test silos.

The steps are

- You should create a test project in your favorite unit testing framework.
- Add references to Microsoft.Orleans.TestingHost and Microsoft.Orleans.OrleansProviders packages from NuGet to the project.
- Reference your interfaces and collection projects in the test project.
- Inherit your test classes from `Orleans.TestingHost.TestingSiloHost`.
- Shutdown the silo properly in each test class.

## The process
The TestingSiloHost creates a mini cluster of 2 silos in 2 different AppDomains and initializes a client in the main AppDomain which test cases will run on, in its constructor.
 Then it will run all of the test cases in the class and at cleanup time shuts the silos down.
The test cases like ordinary grain code should call grains and then wait for the results using await and should not block the execution.

## Writing the code
The `TestingSiloHost` which we inherit from starts the silos up for us but we need to shutdown them ourselves.
The samples here are using MS Test but you can use NUnit, XUnit or any other testing framework that you want. Let's use the hello world sample's code here as an example.

``` csharp
using System;
using System.Threading.Tasks;
using HelloWorldInterfaces;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.TestingHost;

namespace Tests
{
 [TestClass]
    public class HelloWorldSiloTests : TestingSiloHost
    {
        [ClassCleanup]
        public static void ClassCleanup()
        {
            // Optional.
            // By default, the next test class which uses TestingSiloHost will
            // cause a fresh Orleans silo environment to be created.
            StopAllSilos();
        }

        [TestMethod]
        public async Task SayHelloTest()
        {
            // The Orleans silo / client test environment is already set up at this point.

            const long id = 0;
            const string greeting = "Bonjour";

            IHello grain = GrainFactory.GetGrain<IHello>(id);

            // This will create and call a Hello grain with specified 'id' in one of the test silos.
            string reply = await grain.SayHello(greeting);

            Assert.IsNotNull(reply, "Grain replied with some message");
            string expected = string.Format("You said: '{0}', I say: Hello!", greeting);
            Assert.AreEqual(expected, reply, "Grain replied with expected message");
        }
    }
}   

```

Since this test method is asynchronous and leverages the `await` keyword, be sure the method is defined as `async` and returns a `Task`.
As you can see having `ClassCleanup()` is optional but it's good to have it around.
Our test method simply creates a grain, sends the message to it and then first checks if the result is null or not and then checks if it is in the expected format or not.

So writing tests for Orleans is not much different from a normal test project.
You just need to reference the two NuGet packages and derive your class from `TestingSiloHost`.

## Remarks

- Keep in mind that the silo will not be restarted after each test method so if your methods need a fully clean grain then make sure that they'll use different grain IDs.
- The `TestingSiloHost` class has multiple utility methods for starting and stopping silos which can be used for programmatic simulation of some failure situations.
- Starting and stopping the silo for each test case takes time and is not a good idea unless you really need it for a test.
- The two created silos will be named "Primary" and "Secondary". It is important to note that the notion of a Primary silo is only used in Testing. We use the Primary silo to host the system store in a special `MembershipTableGrain` system grain, to allow bootstrap. In production there is no primary and secondary silos: all silos are equal and behave in a peer-to-peer fashion.
- Configurations of the environment are based on `TestSiloOptions` and `TestClientOptions` objects which can be passed to the constructor of the base class.
