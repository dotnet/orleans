using System;
using Orleans.CodeGeneration;
using Orleans.GrainReferences;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    [Serializer(typeof(GrainReference))]
    internal class GrainReferenceSerializer
    {
        private readonly GrainReferenceActivator _activator;

        public GrainReferenceSerializer(GrainReferenceActivator activator)
        {
            _activator = activator;
        }

        /// <summary> Serializer function for grain reference.</summary>
        /// <seealso cref="SerializationManager"/>
        [SerializerMethod]
        public void SerializeGrainReference(object obj, ISerializationContext context, Type expected)
        {
            var writer = context.StreamWriter;
            var input = (GrainReference)obj;
            writer.Write(input.GrainId);
            writer.Write(input.InterfaceType);
        }

        /// <summary> Deserializer function for grain reference.</summary>
        /// <seealso cref="SerializationManager"/>
        [DeserializerMethod]
        public object DeserializeGrainReference(Type t, IDeserializationContext context)
        {
            var reader = context.StreamReader;
            GrainId id = reader.ReadGrainId();
            GrainInterfaceType interfaceType = reader.ReadGrainInterfaceType();

            return _activator.CreateReference(id, interfaceType);
        }

        /// <summary> Copier function for grain reference. </summary>
        /// <seealso cref="SerializationManager"/>
        [CopierMethod]
        public object CopyGrainReference(object original, ICopyContext context)
        {
            return (GrainReference)original;
        }
    }
}
