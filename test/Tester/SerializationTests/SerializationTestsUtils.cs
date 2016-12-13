using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Serialization;
using Xunit;

namespace Tester.SerializationTests
{
    public class SerializationTestsUtils
    {
        public static void VerifyUsingFallbackSerializer(object ob)
        {
            var writer = new BinaryTokenStreamWriter();
            SerializationManager.FallbackSerializer(ob, writer, ob.GetType());
            var bytes = writer.ToByteArray();

            var reader = new BinaryTokenStreamReader(bytes);
            var serToken = reader.ReadToken();
            Assert.Equal(SerializationTokenType.Fallback, serToken);
        }
    }
}
