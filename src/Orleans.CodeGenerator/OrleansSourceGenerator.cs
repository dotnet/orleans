using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Orleans.CodeGenerator.Diagnostics;

namespace Orleans.CodeGenerator
{
    //[Generator]
    public sealed partial class OrleansSerializationSourceGenerator : IIncrementalGenerator
    {

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var processName = Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
            if (processName.Contains("devenv") || processName.Contains("servicehub"))
            {
                return;
            }

            var contextGenerationSpecs = context.AnalyzerConfigOptionsProvider
                .Select(GetCodeGeneratorOptions)
                .Combine(context.CompilationProvider)
                .Combine(context.AnalyzerConfigOptionsProvider)
                .Select(static (tuple, _) =>
                {
                    var codeGenerator = new CodeGenerator(tuple.Left.Right, tuple.Left.Left);
                    return new ContextGenerationSpecs()
                    {
                        LibraryTypes = codeGenerator.LibraryTypes,
                        Compilation = tuple.Left.Right,
                        AnalyzerConfigOptionsProvider = tuple.Right,
                        CodeGeneratorOptions = tuple.Left.Left,
                        MetadataModel = codeGenerator.GenerateMetadataModel(_)


                    };
                });

            context.RegisterSourceOutput(contextGenerationSpecs, RegisterSourceOutput);
        }

        private static CodeGeneratorOptions GetCodeGeneratorOptions(AnalyzerConfigOptionsProvider acop, CancellationToken token)
        {
            try
            {
                var options = new CodeGeneratorOptions();
                if (acop.GlobalOptions.TryGetValue("build_property.orleans_immutableattributes", out var immutableAttributes) && immutableAttributes is { Length: > 0 })
                {
                    options.ImmutableAttributes.AddRange(immutableAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
                }

                if (acop.GlobalOptions.TryGetValue("build_property.orleans_aliasattributes", out var aliasAttributes) && aliasAttributes is { Length: > 0 })
                {
                    options.AliasAttributes.AddRange(aliasAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
                }

                if (acop.GlobalOptions.TryGetValue("build_property.orleans_idattributes", out var idAttributes) && idAttributes is { Length: > 0 })
                {
                    options.IdAttributes.AddRange(idAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
                }

                if (acop.GlobalOptions.TryGetValue("build_property.orleans_generateserializerattributes", out var generateSerializerAttributes) && generateSerializerAttributes is { Length: > 0 })
                {
                    options.GenerateSerializerAttributes.AddRange(generateSerializerAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
                }

                if (acop.GlobalOptions.TryGetValue("build_property.orleans_generatefieldids", out var generateFieldIds) && generateFieldIds is { Length: > 0 })
                {
                    if (Enum.TryParse(generateFieldIds, out GenerateFieldIds fieldIdOption))
                        options.GenerateFieldIds = fieldIdOption;
                }
                return options;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void RegisterSourceOutput(SourceProductionContext context, ContextGenerationSpecs specs)
        {
            try
            {
                if (specs.CodeGeneratorOptions == null)
                {
                    return;
                }

                if (specs.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.orleans_designtimebuild", out var isDesignTimeBuild)
                    && string.Equals("true", isDesignTimeBuild, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (specs.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.orleans_attachdebugger", out var attachDebuggerOption)
                    && string.Equals("true", attachDebuggerOption, StringComparison.OrdinalIgnoreCase))
                {
                    Debugger.Launch();
                }


                Emitter emitter = new Emitter(context, specs);
                emitter.Emit();
            }
            catch (Exception exception) when (HandleException(context, exception))
            {
            }
        }

        private static bool HandleException(SourceProductionContext context, Exception exception)
        {
            if (exception is OrleansGeneratorDiagnosticAnalysisException analysisException)
            {
                context.ReportDiagnostic(analysisException.Diagnostic);
                return true;
            }

            context.ReportDiagnostic(UnhandledCodeGenerationExceptionDiagnostic.CreateDiagnostic(exception));
            return false;
        }

    }
}