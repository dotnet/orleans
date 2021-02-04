using System;

namespace Orleans.Storage
{
    public class GrainStorageSerializer : IGrainStorageSerializer
    {
        private readonly IGrainStorageSerializer _serializer;
        private readonly IGrainStorageSerializer _fallbackDeserializer;

        public GrainStorageSerializer(IGrainStorageSerializer serializer, IGrainStorageSerializer fallbackDeserializer)
        {
            _serializer = serializer;
            _fallbackDeserializer = fallbackDeserializer;
        }

        public BinaryData Serialize<T>(T input) => _serializer.Serialize(input);

        public T Deserialize<T>(BinaryData input)
        {
            try
            {
                return _serializer.Deserialize<T>(input);
            }
            catch (Exception ex1)
            {
                try
                {
                    return _fallbackDeserializer.Deserialize<T>(input);
                }
                catch (Exception ex2)
                {
                    throw new AggregateException("Failed to deserialize input", ex1, ex2);
                }
            }
        }

    }
}
