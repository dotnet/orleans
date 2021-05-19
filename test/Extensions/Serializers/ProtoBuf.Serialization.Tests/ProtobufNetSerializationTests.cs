using FluentAssertions;
using Orleans;
using Orleans.Configuration;
using Orleans.Serialization.ProtobufNet;
using System.IO;
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
