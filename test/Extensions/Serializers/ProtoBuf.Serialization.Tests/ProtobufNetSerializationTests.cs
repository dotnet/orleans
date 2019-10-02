using FluentAssertions;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;
using Orleans.Serialization.ProtobufNet;
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
    public class ProtoBufNetSerializationTests : SerializationTestsBase
    {
        public ProtoBufNetSerializationTests() : base(SerializationTestEnvironment.InitializeWithDefaults(
                 builder => builder.Configure<SerializationProviderOptions>(
                     options => options.SerializationProviders.AddRange(new[] { typeof(ProtobufNetSerializer)}))))
        {

        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization"), TestCategory("ProtoBuf")]
        public void ProtoBufSerializationTest_2_DirectProtoBufNet()
        {
            var person = CreatePerson();
            OtherPerson person2 = null;
            using (MemoryStream stream = new MemoryStream())
            {
                Serializer.Serialize(stream, person);
                stream.Seek(0, SeekOrigin.Begin);
                person2 = Serializer.Deserialize<OtherPerson>(stream);
            }

            Assert.NotSame(person, person2); //The serializer returned an instance of the same object
            person.ShouldBeEquivalentTo(person2);
        }
    }
}
