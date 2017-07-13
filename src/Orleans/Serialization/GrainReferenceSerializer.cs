﻿using System;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    [Serializer(typeof(GrainReference))]
    internal class GrainReferenceSerializer
    {
        /// <summary> Serializer function for grain reference.</summary>
        /// <seealso cref="SerializationManager"/>
        [SerializerMethod]
        protected internal static void SerializeGrainReference(object obj, ISerializationContext context, Type expected)
        {
            var writer = context.StreamWriter;
            var input = (GrainReference)obj;
            writer.Write(input.GrainId);
            if (input.IsSystemTarget)
            {
                writer.Write((byte)1);
                writer.Write(input.SystemTargetSilo);
            }
            else
            {
                writer.Write((byte)0);
            }

            if (input.IsObserverReference)
            {
                input.ObserverId.SerializeToStream(writer);
            }

            // store as null, serialize as empty.
            var genericArg = string.Empty;
            if (input.HasGenericArgument)
                genericArg = input.GenericArguments;
            writer.Write(genericArg);
        }

        /// <summary> Deserializer function for grain reference.</summary>
        /// <seealso cref="SerializationManager"/>
        [DeserializerMethod]
        protected internal static object DeserializeGrainReference(Type t, IDeserializationContext context)
        {
            var reader = context.StreamReader;
            GrainId id = reader.ReadGrainId();
            SiloAddress silo = null;
            GuidId observerId = null;
            byte siloAddressPresent = reader.ReadByte();
            if (siloAddressPresent != 0)
            {
                silo = reader.ReadSiloAddress();
            }
            bool expectObserverId = id.IsClient;
            if (expectObserverId)
            {
                observerId = GuidId.DeserializeFromStream(reader);
            }
            // store as null, serialize as empty.
            var genericArg = reader.ReadString();
            if (string.IsNullOrEmpty(genericArg))
                genericArg = null;

            var runtimeClient = context.AdditionalContext as IRuntimeClient;
            var runtime = runtimeClient?.GrainReferenceRuntime;
            if (expectObserverId)
            {
                return GrainReference.NewObserverGrainReference(id, observerId, runtime);
            }

            return GrainReference.FromGrainId(id, runtime, genericArg, silo);
        }

        /// <summary> Copier function for grain reference. </summary>
        /// <seealso cref="SerializationManager"/>
        [CopierMethod]
        protected internal static object CopyGrainReference(object original, ICopyContext context)
        {
            return (GrainReference)original;
        }
    }
}
