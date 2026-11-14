using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class MetadataSourceOutputGenerator
{
    internal static SourceOutputResult CreateMetadataSourceOutput(
        MetadataAggregateModel metadataModel,
        SourceGeneratorOptions options,
        bool supportsModuleInitializers)
    {
        try
        {
            SourceGeneratorOptionsParser.AttachDebuggerIfRequested(options);
            var metadataGenerator = new MetadataGenerator(metadataModel, metadataModel.AssemblyName, supportsModuleInitializers);
            var metadataNamespace = $"{GeneratedCodeUtilities.CodeGeneratorName}.{Identifier.SanitizeIdentifierName(metadataModel.AssemblyName ?? "Assembly").EscapeIdentifier()}";
            var namespacedMembers = new Dictionary<string, List<Action<SourceWriter>>>(StringComparer.Ordinal);
            GeneratedSourceOutput.AddMember(namespacedMembers, metadataNamespace, metadataGenerator.WriteMetadata);
            var assemblyAttributes = CreateAssemblyAttributes(
                metadataModel.ReferenceAssemblyData.ApplicationParts,
                metadataNamespace,
                metadataGenerator.ClassName);

            var assemblyName = metadataModel.AssemblyName ?? "assembly";
            return SourceOutputResult.FromSource(
                new GeneratedSourceEntry(
                    GeneratedSourceOutput.CreateMetadataHintName(assemblyName),
                    GeneratedSourceOutput.CreateSourceString(namespacedMembers, assemblyAttributes)));
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return SourceOutputResult.FromDiagnostic(analysisException.Diagnostic);
        }
    }

    internal static List<string> CreateAssemblyAttributes(
        IEnumerable<string> applicationParts,
        string metadataNamespace,
        string metadataClassName)
    {
        var assemblyAttributes = ApplicationPartAttributeGenerator.GenerateAttributes("global::Orleans.ApplicationPartAttribute", applicationParts);
        assemblyAttributes.Add($"[assembly: global::Orleans.Serialization.Configuration.TypeManifestProviderAttribute(typeof({metadataNamespace}.{metadataClassName}))]");
        return assemblyAttributes;
    }
}
