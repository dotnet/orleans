using System;
using Orleans.Serialization;

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// A type with an <see cref="IOnDeserialized"/> hook, to test that it is correctly called by the internal serializers.
    /// </summary>
    [Serializable]
    public class TypeWithOnDeserializedHook : IOnDeserialized
    {
        [NonSerialized]
        public ISerializerContext Context;

        public int Int { get; set; }

        void IOnDeserialized.OnDeserialized(ISerializerContext context)
        {
            this.Context = context;
        }
    }
}
