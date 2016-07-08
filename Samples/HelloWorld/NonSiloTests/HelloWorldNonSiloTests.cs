﻿using System;
using System.Linq;
using HelloWorld.Grains;
using HelloWorld.Interfaces;
using Moq;
using Orleans;
using Orleans.Core;
using Orleans.Runtime;
using Xunit;

namespace NonSiloTests
{
    public class HelloWorldNonSiloTests
    {
        [Fact]
        public async void NonSiloSayHelloTest()
        {
            // The mocked Orleans runtime is already set up at this point

            const long id = 0;
            const string greeting = "Bonjour";

            //Create a new instance of the grain. Notice this is the concrete grain type
            //that is being tested, not just the grain interface as with the silo test
            IHello grain = TestGrainFactory.CreateGrain<HelloGrain>(id);

            // This will directly call the grain under test.
            string reply = await grain.SayHello(greeting);

            Assert.NotNull(reply);
            string expected = string.Format("You said: '{0}', I say: Hello!", greeting);
            Assert.Equal(expected, reply);
        }

        [Fact(Skip = "WriteStateAsync currently throws an exception due to a null pointer within Grain.GrainReference. Hopefully fixed in 1.3.0")]
        public async void NonSiloSayHelloArchiveTest()
        {
            // The mocked Orleans runtime is already set up at this point

            const long id = 0;
            const string greeting1 = "Bonjour";
            const string greeting2 = "Hei";

            //Create a new instance of the grain. Notice this is the concrete grain type
            //that is being tested, not just the grain interface as with the silo test
            IHelloArchive grain = TestGrainFactory.CreateGrain<HelloArchiveGrain>(id);

            // This will directly call the grain under test.
            await grain.SayHello(greeting1);
            await grain.SayHello(greeting2);

            var greetings = (await grain.GetGreetings()).ToList();

            Assert.True(greetings.Contains(greeting1));
            Assert.True(greetings.Contains(greeting2));
        }
    }
}
