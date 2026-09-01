using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.SyntaxGeneration;

/// <summary>
/// Produces names for generated private fields which are stable under member reordering and additions,
/// so that .NET Hot Reload sees additions rather than retyped or renamed fields.
/// </summary>
internal static class GeneratedFieldNames
{
    private const int SecondaryHashSeed = unchecked((int)0x9E3779B9);

    public static string Accessor(string prefix, IMemberDescription member)
        => member.IsPrimaryConstructorParameter ? $"{prefix}_{member.FieldId}_ctor" : $"{prefix}_{member.FieldId}";

    public static string[] ForTypes(string prefix, IReadOnlyList<IMemberDescription> members)
    {
        var result = new string[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var key = TryGetTypeKey(member.Type) ?? "Type";
            var identity = $"{member.TypeName.Length}:{member.TypeName}{member.AssemblyName.Length}:{member.AssemblyName}";
            var hash = $"{GeneratedSourceOutput.CreateStableHash(identity)}{GeneratedSourceOutput.CreateStableHash(identity, SecondaryHashSeed)}";
            result[i] = $"{prefix}_{key}_{hash}";
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
