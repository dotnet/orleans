using Orleans.Serialization;
using Xunit;

namespace Tester.SerializationTests
{
    public class SerializationTestsUtils
    {
        public static void VerifyUsingFallbackSerializer(object ob)
        {
            var writer = new SerializationContext
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            SerializationManager.FallbackSerializer(ob, writer, ob.GetType());
            var bytes = writer.StreamWriter.ToByteArray();

            var reader = new BinaryTokenStreamReader(bytes);
            var serToken = reader.ReadToken();
            Assert.Equal(SerializationTokenType.Fallback, serToken);
        }
    }
}
