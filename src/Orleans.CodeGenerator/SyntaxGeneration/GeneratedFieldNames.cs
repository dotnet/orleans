using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.SyntaxGeneration;

/// <summary>
/// Produces names for generated private fields which stay stable when members are added to a type, so that
/// .NET Hot Reload sees additions rather than retyped or renamed fields.
/// </summary>
internal static class GeneratedFieldNames
{
    public static string Accessor(string prefix, IMemberDescription member)
        => member.IsPrimaryConstructorParameter ? $"{prefix}_{member.FieldId}_ctor" : $"{prefix}_{member.FieldId}";

    public static string[] ForTypes(string prefix, IReadOnlyList<IMemberDescription> members)
    {
        if (members.Count == 0)
        {
            return [];
        }

        // phase 0: optimistic pass: try to get a readable name for each type, and see if any collide
        var result = new string[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            result[i] = TryGetTypeKey(members[i].Type) is { } key ? $"{prefix}_{key}" : null!;
        }

        // phase 1: detect collisions and mark them for hashing
        Span<bool> contested = members.Count <= 64 ? stackalloc bool[members.Count] : new bool[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            // we couldn't get a readable name for this type, so we need to hash it
            if (result[i] is null)
            {
                contested[i] = true;
                continue;
            }

            for (var j = i + 1; j < members.Count; j++)
            {
                if (string.Equals(result[i], result[j], StringComparison.Ordinal))
                {
                    contested[i] = true;
                    contested[j] = true;
                }
            }
        }

        // phase 2: for any contested names, replace them with a hash-based name
        for (var i = 0; i < members.Count; i++)
        {
            if (contested[i])
            {
                result[i] = $"{result[i] ?? $"{prefix}_Type"}_{GeneratedSourceOutput.CreateStableHash($"{members[i].TypeName}|{members[i].AssemblyName}")}";
            }
        }

        return result;
    }

    private static string? TryGetTypeKey(ITypeSymbol type)
    {
        try
        {
            return Identifier.SanitizeIdentifierName(type.GetValidIdentifier());
        }
        catch (NotSupportedException)
        {
            // Some types (e.g. pointers) don't have a valid identifier form, so we fall back to the hash-only form.
            // GetValidIdentifier() throws NotSupportedException for these types, so we catch it and return null to indicate that.
            return null;
        }
    }
}
