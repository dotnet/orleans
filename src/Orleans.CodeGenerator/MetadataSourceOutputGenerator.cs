using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class MetadataSourceOutputGenerator
{
    internal static SourceOutputResult CreateMetadataSourceOutput(
        MetadataAggregateModel metadataModel,
        SourceGeneratorOptions options)
    {
        try
        {
            SourceGeneratorOptionsParser.AttachDebuggerIfRequested(options);
            var metadataGenerator = new MetadataGenerator(metadataModel, metadataModel.AssemblyName);
            var metadataClass = metadataGenerator.GenerateMetadata();
            var metadataNamespace = $"{GeneratedCodeUtilities.CodeGeneratorName}.{Identifier.SanitizeIdentifierName(metadataModel.AssemblyName ?? "Assembly")}";
            var namespacedMembers = new Dictionary<string, List<MemberDeclarationSyntax>>(StringComparer.Ordinal);
            GeneratedSourceOutput.AddMember(namespacedMembers, metadataNamespace, metadataClass);
            var assemblyAttributes = CreateAssemblyAttributes(
                metadataModel.ReferenceAssemblyData.ApplicationParts,
                metadataNamespace,
                metadataClass.Identifier.Text);

            var assemblyName = metadataModel.AssemblyName ?? "assembly";
            return SourceOutputResult.FromSource(
                new GeneratedSourceEntry(
                    GeneratedSourceOutput.CreateMetadataHintName(assemblyName),
                    GeneratedSourceOutput.CreateSourceString(GeneratedSourceOutput.CreateCompilationUnit(namespacedMembers, assemblyAttributes))));
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return SourceOutputResult.FromDiagnostic(analysisException.Diagnostic);
        }
    }

    internal static SyntaxList<AttributeListSyntax> CreateAssemblyAttributes(
        IEnumerable<string> applicationParts,
        string metadataNamespace,
        string metadataClassName)
    {
        var assemblyAttributes = ApplicationPartAttributeGenerator.GenerateSyntax(
            SyntaxFactory.ParseName("global::Orleans.ApplicationPartAttribute"),
            applicationParts);
        var metadataAttribute = SyntaxFactory.AttributeList()
            .WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.AssemblyKeyword)))
            .WithAttributes(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(SyntaxFactory.ParseName("global::Orleans.Serialization.Configuration.TypeManifestProviderAttribute"))
                        .AddArgumentListArguments(
                            SyntaxFactory.AttributeArgument(
                                SyntaxFactory.TypeOfExpression(
                                    SyntaxFactory.QualifiedName(
                                        SyntaxFactory.ParseName(metadataNamespace),
                                        SyntaxFactory.IdentifierName(metadataClassName)))))));
        assemblyAttributes.Add(metadataAttribute);

        return SyntaxFactory.List(assemblyAttributes);
    }
}

