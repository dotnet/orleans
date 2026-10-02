using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator;

internal static class ReferencedSerializerImplementation
{
    private const string ContractPrefix = "OrleansCodeGen.FieldAccessors.v1:";
    private const string StaticContract = ContractPrefix + "Static";
    private const string DynamicContract = ContractPrefix + "Dynamic";

    internal static AttributeListSyntax GetAccessorContract(
        IEnumerable<SerializerGenerator.GeneratedFieldDescription> fields)
    {
        var dynamicAccessors = fields.Any(static field =>
            field is SerializerGenerator.FieldAccessorDescription { InitializationSyntax: not null });
        return AttributeList(SingletonSeparatedList(
            Attribute(ParseName("global::System.ComponentModel.DescriptionAttribute"))
                .AddArgumentListArguments(AttributeArgument(
                    (dynamicAccessors ? DynamicContract : StaticContract).GetLiteralExpression()))));
    }

    internal static string? Validate(INamedTypeSymbol implementation)
    {
        // Private accessor fields are absent from reference assemblies. Only producer metadata can attest
        // to the implementation's accessor strategy independently of the consumer's target and options.
        var contract = implementation.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute"
            && attribute.ConstructorArguments.Length == 1
            && attribute.ConstructorArguments[0].Value is string value
            && value.StartsWith(ContractPrefix, StringComparison.Ordinal));
        return contract?.ConstructorArguments[0].Value switch
        {
            StaticContract => null,
            DynamicContract => $"referenced implementation '{implementation.Name}' requires statically generated field accessor support",
            _ => $"referenced implementation '{implementation.Name}' has no supported generated field accessor contract; rebuild the referenced assembly with the current Orleans generator",
        };
    }
}
