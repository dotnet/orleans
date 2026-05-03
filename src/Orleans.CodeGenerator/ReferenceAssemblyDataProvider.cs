using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator;

internal static class ReferenceAssemblyDataProvider
{
    internal static ReferenceAssemblyDataResult CreateReferenceAssemblyDataResult(
        Compilation compilation,
        SourceGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var model = ModelExtractor.ExtractReferenceAssemblyData(
                compilation,
                SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options),
                cancellationToken,
                out var diagnostics);

            return ReferenceAssemblyDataResult.FromModelAndDiagnostics(model, diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return ReferenceAssemblyDataResult.FromModelAndDiagnostics(
                ReferenceAssemblyDataProvider.CreateEmptyReferenceAssemblyModel(compilation.AssemblyName ?? string.Empty),
                [analysisException.Diagnostic]);
        }
    }

    internal static ReferenceAssemblyModel CreateEmptyReferenceAssemblyModel(string assemblyName)
        => new(
            assemblyName,
            EquatableArray<string>.Empty,
            EquatableArray<WellKnownTypeIdModel>.Empty,
            EquatableArray<TypeAliasModel>.Empty,
            EquatableArray<CompoundTypeAliasModel>.Empty,
            EquatableArray<SerializableTypeModel>.Empty,
            EquatableArray<ProxyInterfaceModel>.Empty,
            EquatableArray<RegisteredCodecModel>.Empty,
            EquatableArray<InterfaceImplementationModel>.Empty);
}

