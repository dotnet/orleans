using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Google.Protobuf;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Serialization
{
    public class ProtobufSerializationTests
    {
        public ProtobufSerializationTests()
        {
            SerializationManager.InitializeForTesting(new List<TypeInfo> { typeof(ProtobufSerializer).GetTypeInfo() });
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("Protobuf")]
        public void ProtobufSerializationTest_1_DirectProto()
        {
            AddressBook book = CreateAddressBook();
            byte[] bytes;
            using (MemoryStream stream = new MemoryStream())
            {
                book.WriteTo(stream);
                bytes = stream.ToArray();
            }
            AddressBook restored = AddressBook.Parser.ParseFrom(bytes);

            Assert.NotSame(book, restored); //The serializer returned an instance of the same object
            Assert.Equal(1, restored.People.Count); //The serialization didn't preserve the same number of inner values
            Assert.Equal(book.People[0], restored.People[0]); //The serialization didn't preserve the proper inner value
            Assert.Equal(book, restored); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("Protobuf")]
        public void ProtobufSerializationTest_2_RegularOrleansSerializationStillWorks()
        {
            var input = new OrleansType();
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.NotSame(input, output); //The serializer returned an instance of the same object
            Assert.Equal(input, output); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("Protobuf")]
        public void ProtobufSerializationTest_3_ProtoSerialization()
        {
            var input = CreateAddressBook();
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.NotSame(input, output); //The serializer returned an instance of the same object
            Assert.Equal(input, output); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("Protobuf")]
        public void ProtobufSerializationTest_4_ProtoSerialization()
        {
            var input = CreateCounter();
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.NotSame(input, output); //The serializer returned an instance of the same object
            Assert.Equal(input, output); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("Protobuf")]
        public void ProtobufSerializationTest_5_DeepCopy()
        {
            var input = CreateAddressBook();
            var output = SerializationManager.DeepCopy(input);
            Assert.NotSame(input, output); //The serializer returned an instance of the same object
            Assert.Equal(input, output); //The serialization didn't preserve the proper value
        }

		[Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("Protobuf")]
		public void ProtobufSerializationTest_6_DefaultMessageSerialization()
		{
			var input = CreateDefaultCounter();
			var output = SerializationManager.RoundTripSerializationForTesting(input);
			Assert.NotSame(input, output); //The serializer returned an instance of the same object
			Assert.Equal(input, output); //The serialization didn't preserve the proper value
		}

		private Counter CreateCounter()
        {
            Counter counter = new Counter();
            counter.Id = 1;
            counter.Name = "Foo";
            return counter;
        }

		private Counter CreateDefaultCounter()
		{
			Counter counter = new Counter();
			counter.Id = 0;
			counter.Name = "";
			return counter;
		}

		private AddressBook CreateAddressBook()
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

        [Serializable]
        public class OrleansType
        {
            public int val = 33;

            public override bool Equals(object obj)
            {
                var o = obj as OrleansType;
                return o != null && val.Equals(o.val);
            }

            public override int GetHashCode()
            {
                return val;
            }
        }
    }
}
