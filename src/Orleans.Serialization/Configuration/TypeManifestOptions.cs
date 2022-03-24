using System;
using System.Collections.Generic;

namespace Orleans.Serialization.Configuration
{
    /// <summary>
    /// Configuration of all types which are known to the code generator.
    /// </summary>
    public sealed class TypeManifestOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether <see cref="SerializerConfigurationAnalyzer"/> should be enabled.
        /// </summary>
        /// <remarks>
        /// This property does not cause <see cref="SerializerConfigurationAnalyzer"/> to be invoked.
        /// That is the responsibility of the consuming framework.
        /// </remarks>
        public bool? EnableConfigurationAnalysis { get; set; }

        /// <summary>
        /// Gets the set of known activators, which are responsible for creating instances of a given type.
        /// </summary>
        public HashSet<Type> Activators { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of known field codecs, which are responsible for serializing and deserializing fields of a given type.
        /// </summary>
        public HashSet<Type> FieldCodecs { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of known serializers, which are responsible for serializing and deserializing a given type.
        /// </summary>
        public HashSet<Type> Serializers { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of copiers, which are responsible for creating deep copies of a given type.
        /// </summary>
        public HashSet<Type> Copiers { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of known interfaces, which are interfaces that have corresponding proxies in the <see cref="InterfaceProxies"/> collection.
        /// </summary>
        public HashSet<Type> Interfaces { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of known interface proxies, which capture method invocations which can be serialized, deserialized, and invoked against an implementation of this interface.
        /// </summary>
        /// <remarks>
        /// This allows decoupling the caller and target, so that remote procedure calls can be implemented by capturing an invocation, transmitting it, and later invoking it against a target object.
        /// </remarks>
        public HashSet<Type> InterfaceProxies { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the set of interface implementations, which are implementations of the interfaces present in <see cref="Interfaces"/>.
        /// </summary>
        public HashSet<Type> InterfaceImplementations { get; } = new HashSet<Type>();

        /// <summary>
        /// Gets the mapping of well-known type identifiers to their corresponding type.
        /// </summary>
        public Dictionary<uint, Type> WellKnownTypeIds { get; } = new Dictionary<uint, Type>();

        /// <summary>
        /// Gets the mapping of well-known type aliases to their corresponding type.
        /// </summary>
        public Dictionary<string, Type> WellKnownTypeAliases { get; } = new Dictionary<string, Type>();

        /// <summary>
        /// Gets the mapping of allowed type names.
        /// </summary>
        public HashSet<string> AllowedTypes { get; } = new HashSet<string>(StringComparer.Ordinal);
    }
}