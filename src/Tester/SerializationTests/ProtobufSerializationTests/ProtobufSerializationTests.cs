using System.IO;
using System.Collections.Generic;
using System.Reflection;

using Google.Protobuf;
using Orleans.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace UnitTests.Serialization
{
    [TestClass]
    [DeploymentItem("OrleansGoogleUtils.dll")]
    public class ProtobufSerializationTests
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting(new List<TypeInfo> { typeof(ProtobufSerializer).GetTypeInfo() });
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("Protobuf")]
        public void ProtobuffSerializationTest_1_DirectProto()
        {
            AddressBook book = CreateAddressBook();
            byte[] bytes;
            using (MemoryStream stream = new MemoryStream())
            {
                book.WriteTo(stream);
                bytes = stream.ToArray();
            }
            AddressBook restored = AddressBook.Parser.ParseFrom(bytes);

            Assert.AreNotSame(book, restored, "The serializer returned an instance of the same object");
            Assert.AreEqual(1, restored.People.Count, "The serialization didn't preserve the same number of inner values");
            Assert.AreEqual(book.People[0], restored.People[0], "The serialization didn't preserve the proper inner value");
            Assert.AreEqual(book, restored, "The serialization didn't preserve the proper value");            
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("Protobuf")]
        public void ProtobuffSerializationTest_2_RegularOrleansSerializationStillWorks()
        {
            var input = new OrleansType();
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.AreNotSame(input, output, "The serializer returned an instance of the same object");
            Assert.AreEqual(input, output, "The serialization didn't preserve the proper value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("Protobuf")]
        public void ProtobuffSerializationTest_3_ProtoSerialization()
        {
            var input = CreateAddressBook();
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.AreNotSame(input, output, "The serializer returned an instance of the same object");
            Assert.AreEqual(input, output, "The serialization didn't preserve the proper value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("Protobuf")]
        public void ProtobuffSerializationTest_4_ProtoSerialization()
        {
            var input = CreateCounter();
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.AreNotSame(input, output, "The serializer returned an instance of the same object");
            Assert.AreEqual(input, output, "The serialization didn't preserve the proper value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization"), TestCategory("Protobuf")]
        public void ProtobuffSerializationTest_5_DeepCopy()
        {
            var input = CreateAddressBook();
            var output = SerializationManager.DeepCopy(input);
            Assert.AreNotSame(input, output, "The serializer returned an instance of the same object");
            Assert.AreEqual(input, output, "The serialization didn't preserve the proper value");
        }

        private Counter CreateCounter()
        {
            Counter counter = new Counter();
            counter.Id = 1;
            counter.Name = "Foo";
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
