using System;
using Orleans.Utilities;

namespace Orleans.Serialization
{
    internal static class SerializerFeatureExtensions
    {
        /// <summary>
        /// Adds <paramref name="type"/> as a known type.
        /// </summary>
        /// <param name="serializerFeature">The serializer feature.</param>
        /// <param name="type">The type.</param>
        public static void AddKnownType(this SerializerFeature serializerFeature, Type type)
        {
            serializerFeature.KnownTypes.Add(new SerializerKnownTypeMetadata(RuntimeTypeNameFormatter.Format(type), type.OrleansTypeKeyString()));
        }

        /// <summary>
        /// Adds serialization delegates for <paramref name="type"/>.
        /// </summary>
        /// <param name="serializerFeature">The serializer feature.</param>
        /// <param name="type">The type.</param>
        /// <param name="copier">The copy delegate.</param>
        /// <param name="serializer">The serializer delegate.</param>
        /// <param name="deserializer">The deserializer delegate.</param>
        public static void AddSerializerDelegates(this SerializerFeature serializerFeature, Type type, DeepCopier copier, Serializer serializer, Deserializer deserializer)
        {
            serializerFeature.SerializerDelegates.Add(new SerializerDelegateMetadata(type, copier, serializer, deserializer, overrideExisting: true));
        }

        /// <summary>
        /// Adds serialization delegates for <paramref name="type"/>.
        /// </summary>
        /// <param name="serializerFeature">The serializer feature.</param>
        /// <param name="type">The type.</param>
        /// <param name="copier">The copy delegate.</param>
        /// <param name="serializer">The serializer delegate.</param>
        /// <param name="deserializer">The deserializer delegate.</param>
        /// <param name="overrideExisting">Whether or not to override existing registrations.</param>
        public static void AddSerializerDelegates(this SerializerFeature serializerFeature, Type type, DeepCopier copier, Serializer serializer, Deserializer deserializer, bool overrideExisting)
        {
            serializerFeature.SerializerDelegates.Add(new SerializerDelegateMetadata(type, copier, serializer, deserializer, overrideExisting));
        }
    }
}