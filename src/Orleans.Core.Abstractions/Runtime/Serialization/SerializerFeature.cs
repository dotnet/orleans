using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Orleans.Serialization
{
    /// <summary>
    /// Contains metadata about serializers and serialization types.
    /// </summary>
    public class SerializerFeature
    {
        /// <summary>
        /// Gets a collection of metadata about types which contain serializer methods for individual types.
        /// </summary>
        /// <remarks>
        /// This collection corresponds to the <see cref="Orleans.CodeGeneration.SerializerAttribute"/> attribute as well as types which are self-serializing.
        /// </remarks>
        public IList<SerializerTypeMetadata> SerializerTypes { get; } = new List<SerializerTypeMetadata>();

        /// <summary>
        /// Gets a collection of metadata about delegates used to serialize individual types.
        /// </summary>
        /// <remarks>
        /// This collection is intended to hold information about built-in serializers which are represented as a collection of delegates.
        /// </remarks>
        public IList<SerializerDelegateMetadata> SerializerDelegates { get; } = new List<SerializerDelegateMetadata>();

        /// <summary>
        /// Gets a collection of metadata about types which may be serializable.
        /// </summary>
        public IList<SerializerKnownTypeMetadata> KnownTypes { get; } = new List<SerializerKnownTypeMetadata>();

        /// <summary>
        /// Adds a serializer type.
        /// </summary>
        public void AddSerializerType(Type targetType, Type serializerType)
        {
            this.SerializerTypes.Add(new SerializerTypeMetadata(targetType, serializerType, overrideExisting: true));
        }

        /// <summary>
        /// Adds a serializer type.
        /// </summary>
        public void AddSerializerType(Type targetType, Type serializerType, bool overrideExisting)
        {
            this.SerializerTypes.Add(new SerializerTypeMetadata(targetType, serializerType, overrideExisting));
        }

        /// <summary>
        /// Adds a known type to the <see cref="KnownTypes"/> property.
        /// </summary>
        /// <param name="fullyQualifiedTypeName">the fully-qualified type name.</param>
        /// <param name="typeKey">The orleans type key.</param>
        public void AddKnownType(string fullyQualifiedTypeName, string typeKey)
        {
            this.KnownTypes.Add(new SerializerKnownTypeMetadata(fullyQualifiedTypeName, typeKey));
        }
    }

    /// <summary>
    /// Describes a class which has serialization methods.
    /// </summary>
    [DebuggerDisplay("Serializer: {" + nameof(Serializer) + "}, Target: {"+ nameof(Target) + "}")]
    public class SerializerTypeMetadata
    {
        public SerializerTypeMetadata(Type target, Type serializer, bool overrideExisting = true)
        {
            this.Serializer = serializer;
            this.Target = target;
            this.OverrideExisting = overrideExisting;
        }

        /// <summary>
        /// Gets the serializer type.
        /// </summary>
        public Type Serializer { get; }

        /// <summary>
        /// Gets the target type.
        /// </summary>
        public Type Target { get; }

        /// <summary>
        /// Whether or not to override an existing registration for the provided <see cref="Target"/>.
        /// </summary>
        public bool OverrideExisting { get; }
    }

    /// <summary>
    /// Describes a set of delegates which are used to serializer a specified type.
    /// </summary>
    [DebuggerDisplay("Target: {" + nameof(Target) + "}")]
    public class SerializerDelegateMetadata
    {
        public SerializerDelegateMetadata(Type target, DeepCopier deepCopier, Serializer serializer, Deserializer deserializer, bool overrideExisting = true)
        {
            this.Target = target;
            this.OverrideExisting = overrideExisting;
            this.Delegates = new SerializerMethods(deepCopier, serializer, deserializer);
        }

        /// <summary>
        /// Gets the target type.
        /// </summary>
        public Type Target { get; }

        /// <summary>
        /// Whether or not to override entries which 
        /// </summary>
        public bool OverrideExisting { get; }

        /// <summary>
        /// Gets the serialization delegates.
        /// </summary>
        public SerializerMethods Delegates { get; }
    }

    /// <summary>
    /// Describes a type which can be identified by the serializer.
    /// </summary>
    public class SerializerKnownTypeMetadata
    {
        public SerializerKnownTypeMetadata(string type, string typeKey)
        {
            this.Type = type;
            this.TypeKey = typeKey;
        }

        /// <summary>
        /// Gets the type key.
        /// </summary>
        public string TypeKey { get; }

        /// <summary>
        /// Gets the fully-qualified type name.
        /// </summary>
        public string Type { get; }
    }
}
