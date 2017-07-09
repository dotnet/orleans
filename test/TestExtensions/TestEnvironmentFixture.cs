using Orleans.Serialization;

namespace TestExtensions
{
    public class TestEnvironmentFixture : SerializationTestEnvironment
    {
        public const string DefaultCollection = "DefaultTestEnvironment";

        public T RoundTripSerialization<T>(T source)
        {
            BinaryTokenStreamWriter writer = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(source, writer);
            T output = (T)SerializationManager.Deserialize(new BinaryTokenStreamReader(writer.ToByteArray()));

            return output;
        }
    }
}