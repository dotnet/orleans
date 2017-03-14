using Orleans;
using Orleans.Serialization;
using Xunit;

namespace Tester.SerializationTests
{
    public class SerializationTestsUtils
    {
        public static void VerifyUsingFallbackSerializer(SerializationManager serializationManager, object ob)
        {
            var writer = new SerializationContext(serializationManager)
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            serializationManager.FallbackSerializer(ob, writer, ob.GetType());
            var bytes = writer.StreamWriter.ToByteArray();

            var reader = new BinaryTokenStreamReader(bytes);
            var serToken = reader.ReadToken();
            Assert.Equal(SerializationTokenType.Fallback, serToken);
        }
    }
}
