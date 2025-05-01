using System.IO;
using Orleans.Storage;

namespace Orleans.Persistence.Cosmos.Serialization
{
    internal class OrleansCosmosSerializer : CosmosSerializer
    {
        private readonly IGrainStorageSerializer _serializer;

        public OrleansCosmosSerializer(IGrainStorageSerializer serializer)
        {
            _serializer = serializer;
        }

        public override T FromStream<T>(Stream stream)
        {
            using (stream)
            {
                var binaryData = BinaryData.FromStream(stream);
                return _serializer.Deserialize<T>(binaryData);
            }
        }

        public override Stream ToStream<T>(T input)
        {
            var binaryData = _serializer.Serialize(input);
            return binaryData.ToStream();
        }
    }
}
