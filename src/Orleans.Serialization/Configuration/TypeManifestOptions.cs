using System;
using System.Collections.Generic;

namespace Orleans.Serialization.Configuration
{
    public sealed class TypeManifestOptions
    {
        public HashSet<Type> Activators { get; } = new HashSet<Type>();

        public HashSet<Type> FieldCodecs { get; } = new HashSet<Type>();

        public HashSet<Type> Serializers { get; } = new HashSet<Type>();

        public HashSet<Type> Copiers { get; } = new HashSet<Type>();

        public HashSet<Type> Interfaces { get; } = new HashSet<Type>();

        public HashSet<Type> InterfaceProxies { get; } = new HashSet<Type>();

        public HashSet<Type> InterfaceImplementations { get; } = new HashSet<Type>();

        public Dictionary<uint, Type> WellKnownTypeIds { get; } = new Dictionary<uint, Type>();

        public Dictionary<string, Type> WellKnownTypeAliases { get; } = new Dictionary<string, Type>();

        public HashSet<string> AllowedTypes { get; } = new HashSet<string>(StringComparer.Ordinal);
    }
}