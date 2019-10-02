using Google.Protobuf;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

namespace ProtoBuf.Serialization.Tests
{
    public class GoogleProtoBufSerializationTests : SerializationTestsBase
    {
        public GoogleProtoBufSerializationTests() : base(SerializationTestEnvironment.InitializeWithDefaults(
            builder => builder.Configure<SerializationProviderOptions>(
                options => options.SerializationProviders.Add(typeof(ProtobufSerializer)))))
        {

        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization"), TestCategory("ProtoBuf")]
        public void ProtoBufSerializationTest_1_DirectGoogleProtoBuf()
        {
            var book = CreateAddressBook();
            byte[] bytes;
            using (MemoryStream stream = new MemoryStream())
            {
                book.WriteTo(stream);
                bytes = stream.ToArray();
            }
            AddressBook restored = AddressBook.Parser.ParseFrom(bytes);

            Assert.NotSame(book, restored); //The serializer returned an instance of the same object
            Assert.Single(restored.People); //The serialization didn't preserve the same number of inner values
            Assert.Equal(book.People[0], restored.People[0]); //The serialization didn't preserve the proper inner value
            Assert.Equal(book, restored); //The serialization didn't preserve the proper value
        }
    }
}
