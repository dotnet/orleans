using Orleans.Serialization.ProtobufNet;
using System;
using System.Reflection;
using TestExtensions;
using Orleans.Runtime.Configuration;
using Xunit;
using System.IO;
using FluentAssertions;

namespace Orleans.ProtobufNet.Tests
{
    public class ProtobufNetTests
    {
        private readonly SerializationTestEnvironment environment;

        public ProtobufNetTests()
        {
            this.environment = SerializationTestEnvironment.InitializeWithDefaults(
                new ClientConfiguration
                {
                    SerializationProviders =
                    {
                        typeof(ProtobufNetSerializer).GetTypeInfo()
                    }
                });
        }

        private Person CreatePerson()
        {
            var person = new Person
            {
                Id = 12345,
                Name = "Fred",
                Address = new Address
                {
                    Line1 = "Flat 1",
                    Line2 = "The Meadows"
                }
            };
            return person;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("ProtobufNet")]
        public void ProtobufSerializationTest_2_RegularOrleansSerializationStillWorks()
        {
            var input = new OrleansType();
            var output = this.environment.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.NotSame(input, output); //The serializer returned an instance of the same object
            input.ShouldBeEquivalentTo(output); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("ProtobufNet")]
        public void ProtobufSerializationTest_3_ProtoSerialization()
        {
            var input = CreatePerson();
            var output = this.environment.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.NotSame(input, output); //The serializer returned an instance of the same object
            input.ShouldBeEquivalentTo(output); //The serialization didn't preserve the proper value
        }
    }
}
