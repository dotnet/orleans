using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.SerializerGenerator;

namespace Orleans.CodeGenerator;

/// <summary>
/// Describes construction using the same ordered service fields as codec and copier emission.
/// Array requests retain their definition's type parameters so closed contexts preserve concrete service selection.
/// </summary>
internal sealed class GeneratedTypeConstruction
{
    private readonly List<GeneratedFieldDescription> _fields;

    public GeneratedTypeConstruction(
        LibraryTypes libraryTypes,
        List<GeneratedFieldDescription> fields,
        bool hasBaseContract,
        bool requiresCodecProvider = false)
    {
        _fields = fields;
        HasBaseContract = hasBaseContract;
        var codecProviderAdded = false;
        if (requiresCodecProvider)
            AddCodecProvider();

        foreach (var field in fields)
        {
            if (field.IsInjected)
                Parameters.Add(new(field.FieldType, field.FieldName));
            else if (field is CodecProviderFieldDescription or CodecFieldDescription or BaseCodecFieldDescription
                or CopierGenerator.CopierFieldDescription or CopierGenerator.BaseCopierFieldDescription)
                AddCodecProvider();
        }

        void AddCodecProvider()
        {
            if (codecProviderAdded) return;
            Parameters.Add(new(libraryTypes.ICodecProvider.ToTypeSyntax(), "codecProvider", IsCodecProvider: true));
            codecProviderAdded = true;
        }
    }

    public List<ConstructorParameter> Parameters { get; } = [];
    public bool HasBaseContract { get; }

    public IEnumerable<IArrayTypeSymbol> CanonicalArrays
    {
        get
        {
            foreach (var description in _fields)
            {
                var underlyingType = description switch
                {
                    CodecFieldDescription codec => codec.UnderlyingType,
                    CopierGenerator.CopierFieldDescription copier => copier.UnderlyingType,
                    _ => null
                };
                if (underlyingType is IArrayTypeSymbol { IsSZArray: true } array)
                    yield return array;
            }
        }
    }

    internal readonly record struct ConstructorParameter(TypeSyntax Type, string Name, bool IsCodecProvider = false)
    {
        public ParameterSyntax ToSyntax() => Parameter(Name.ToIdentifier()).WithType(Type);
    }
}
