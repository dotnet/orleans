using Orleans.Serialization.TypeSystem;
using System.Collections.Concurrent;
using System.Linq;

namespace Orleans.Dashboard.Metrics.TypeFormatting;

internal sealed class TypeFormatter
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string Parse(string typeName) => Cache.GetOrAdd(typeName, Format);

    private static string Format(string typeName)
    {
        var typeInfo = RuntimeTypeNameParser.Parse(typeName);

        return Format(typeInfo);
    }

    private static string Format(TypeSpec typeSpec)
    {
        switch (typeSpec)
        {
            case AssemblyQualifiedTypeSpec qualified:
                return Format(qualified.Type);
            case ConstructedGenericTypeSpec constructed:
                return $"{Format(constructed.UnconstructedType)}<{string.Join(", ", constructed.Arguments.Select(Format))}>";
            default:
                var name = typeSpec.Format();

                const string SystemPrefix = "System.";

                if (name.StartsWith(SystemPrefix))
                {
                    name = name[SystemPrefix.Length..];
                }

                var genericCardinalityIndex = name.IndexOf('`');

                if (genericCardinalityIndex > 0)
                {
                    name = name[..genericCardinalityIndex];
                }

                return name;
        }
    }
}
