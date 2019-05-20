using Orleans;
using Orleans.Serialization;
using Xunit;

namespace Tester.SerializationTests
{
    public class SerializationTestsUtils
    {
        public static void VerifyUsingFallbackSerializer(SerializationManager serializationManager, object ob)
        {
            var writer = new BinaryTokenStreamWriter();
            var context = new SerializationContext(serializationManager)
            {
                StreamWriter = writer
            };
            serializationManager.FallbackSerializer(ob, context, ob.GetType());
            var bytes = writer.ToByteArray();

            var reader = new BinaryTokenStreamReader(bytes);
            var serToken = reader.ReadToken();
            Assert.Equal(SerializationTokenType.Fallback, serToken);
        }
    }
}
