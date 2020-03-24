using System;
using Orleans.Serialization;

namespace Orleans.Persistence.S3 {
    internal static class SerializationManagerExtensions
    {

        public static object Deserialize(this SerializationManager serializationManager, ArraySegment<byte> data) =>
            data.Count > 0
                ? serializationManager.Deserialize(new BinaryTokenStreamReader(data))
                : null;

        public static void DeserializeToState(this SerializationManager serializationManager, IGrainState grainState, ArraySegment<byte> data) =>
            grainState.State =
                serializationManager.Deserialize(data)
                ?? grainState.CreateDefaultState();

        public static ArraySegment<byte> SerializeFromState(this SerializationManager serializationManager, IGrainState grainState)
        {
            var writer = new BinaryTokenStreamWriter();
            serializationManager.Serialize(grainState.State, writer);
            return writer
                .ToBytes()
                .MergeToSingleSegment();
        }
    }
}