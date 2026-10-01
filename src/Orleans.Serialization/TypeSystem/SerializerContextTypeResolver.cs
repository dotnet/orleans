using System;
using System.Collections.Generic;
using Orleans.Serialization.Configuration;

namespace Orleans.Serialization.TypeSystem;

internal sealed class SerializerContextTypeResolver : TypeResolver
{
    private readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);

    public SerializerContextTypeResolver(TypeManifestOptions manifest)
    {
        foreach (var type in manifest.ContextTypes)
        {
            var name = RuntimeTypeNameFormatter.FormatInternalNoCache(type, allowAliases: false);
            _types.Add(name, type);
            var unqualified = RuntimeTypeNameParser.Parse(name) is AssemblyQualifiedTypeSpec qualified
                ? qualified.Type.Format()
                : name;
            _types.TryAdd(unqualified, type);
        }
    }

    public override Type ResolveType(string name)
        => TryResolveType(name, out var type) ? type
            : throw new TypeAccessException($"Type '{name}' is missing from the registered serializer contexts.");

    public override bool TryResolveType(string name, out Type type)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A type name is required.", nameof(name));
        return _types.TryGetValue(name, out type!);
    }
}
