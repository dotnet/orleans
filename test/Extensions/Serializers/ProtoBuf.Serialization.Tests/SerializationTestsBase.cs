using Orleans.Serialization.ProtobufNet;
using System;
using System.Reflection;
using TestExtensions;
using Orleans.Runtime.Configuration;
using Xunit;
using System.IO;
using FluentAssertions;
using Google.Protobuf;

namespace ProtoBuf.Serialization.Tests
{
    public abstract class SerializationTestsBase
    {
        private readonly SerializationTestEnvironment _environment;

        public SerializationTestsBase(SerializationTestEnvironment environment)
        {
            if (environment == null)
                throw new ArgumentNullException(nameof(environment));

            _environment = environment;
        }

        protected OtherPerson CreatePerson()
        {
            var person = new OtherPerson
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

        protected ImmutablePerson CreateImmutablePerson()
        {
            var person = new ImmutablePerson
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

        protected Counter CreateCounter()
        {
            Counter counter = new Counter();
            counter.Id = 1;
            counter.Name = "Foo";
            return counter;
        }

        protected Counter CreateDefaultCounter()
        {
            Counter counter = new Counter();
            counter.Id = 0;
            counter.Name = "";
            return counter;
        }

        protected AddressBook CreateAddressBook()
        {
            Person person = new Person
            {
                Id = 1,
                Name = "Foo",
                Email = "foo@bar",
                Phones = { new Person.Types.PhoneNumber { Number = "555-1212" } }
            };
            person.Id = 2;
            AddressBook book = new AddressBook
            {
                People = { person },
                AddressBookName = "MyGreenAddressBook"
            };
            return book;
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization"), TestCategory("ProtoBuf")]
        public void ProtoBufSerializationTest_3_RegularOrleansSerializationStillWorks()
        {
            var input = new OrleansType();
            var output = this._environment.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.NotSame(input, output); //The serializer returned an instance of the same object
            input.ShouldBeEquivalentTo(output); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization"), TestCategory("ProtoBuf")]
        public void ProtoBufSerializationTest_4_OtherPersonProtoBufType()
        {
            var input = CreatePerson();
            var output = this._environment.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.NotSame(input, output); //The serializer returned an instance of the same object
            input.ShouldBeEquivalentTo(output); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization"), TestCategory("ProtoBuf")]
        public void ProtoBufSerializationTest_5_CounterProtoBufSerialization()
        {
            var input = CreateCounter();
            var output = this._environment.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.NotSame(input, output); //The serializer returned an instance of the same object
            input.ShouldBeEquivalentTo(output); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization"), TestCategory("ProtoBuf")]
        public void ProtoBufSerializationTest_6_DeepCopy()
        {
            var input = CreatePerson();
            var output = this._environment.SerializationManager.DeepCopy(input);
            Assert.NotSame(input, output); //The serializer returned an instance of the same object
            input.ShouldBeEquivalentTo(output); ; //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization"), TestCategory("ProtoBuf")]
        public void ProtoBufSerializationTest_7_DeepCopyImmutableType()
        {
            var input = CreateImmutablePerson();
            var output = this._environment.SerializationManager.DeepCopy(input);
            Assert.Same(input, output); //The serializer returned an instance of the same object
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization"), TestCategory("ProtoBuf")]
        public void ProtoBufSerializationTest_8_DefaultCounterMessageSerialization()
        {
            var input = CreateDefaultCounter();
            var output = this._environment.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.NotSame(input, output); //The serializer returned an instance of the same object
            input.ShouldBeEquivalentTo(output); //The serialization didn't preserve the proper value
        }
    }
}
