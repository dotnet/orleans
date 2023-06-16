using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Orleans.CodeGenerator.Diagnostics;

namespace Orleans.CodeGenerator
{
    [Generator]
    public class OrleansSerializationSourceGenerator : IIncrementalGenerator
    {

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var processName = Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
            if (processName.Contains("devenv") || processName.Contains("servicehub"))
            {
                return;
            }

            var codeGeneratorOptions = context.AnalyzerConfigOptionsProvider.Select(GetCodeGeneratorOptions);

            context.RegisterSourceOutput(codeGeneratorOptions.Combine(context.AnalyzerConfigOptionsProvider).Combine(context.CompilationProvider), RegisterSourceOutput);
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

        private static void RegisterSourceOutput(SourceProductionContext context, ((CodeGeneratorOptions codeGeneratorOptions, AnalyzerConfigOptionsProvider analyzerConfigOptionsProvider) Left, Compilation compilation) ctx)
        {
            try
            {
                if (ctx.Left.codeGeneratorOptions == null)
                {
                    return;
                }

                if (ctx.Left.analyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.orleans_designtimebuild", out var isDesignTimeBuild)
                    && string.Equals("true", isDesignTimeBuild, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (ctx.Left.analyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.orleans_attachdebugger", out var attachDebuggerOption)
                    && string.Equals("true", attachDebuggerOption, StringComparison.OrdinalIgnoreCase))
                {
                    Debugger.Launch();
                }

                var codeGenerator = new CodeGenerator(ctx.compilation, ctx.Left.codeGeneratorOptions);
                var syntax = codeGenerator.GenerateCode(context.CancellationToken);
                var sourceString = syntax.NormalizeWhitespace().ToFullString();
                var sourceText = SourceText.From(sourceString, Encoding.UTF8);
                context.AddSource($"{ctx.compilation.AssemblyName ?? "assembly"}.orleans.g.cs", sourceText);
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