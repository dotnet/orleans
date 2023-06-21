namespace Orleans.CodeGenerator.Generators;

using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Orleans.CodeGenerator.Diagnostics;

internal abstract class BaseIncrementalGenerator : IIncrementalGenerator
{
    public static IncrementalValueProvider<CodeGeneratorOptions> CodeGeneratorOptions { get; private set; }
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {

        context.RegisterPostInitializationOutput(PostInitializationOutputCallback);

        AddSyntaxProvider(context.SyntaxProvider);


        CodeGeneratorOptions = context.AnalyzerConfigOptionsProvider.Select(GetCodeGeneratorOptions);

        var incrementalGeneratorContext = Execute(context);


        context.RegisterSourceOutput(incrementalGeneratorContext, RegisterSourceOutputPrivate);
    }

    protected virtual void PostInitializationOutputCallback(IncrementalGeneratorPostInitializationContext context) { }

    private void RegisterSourceOutputPrivate(SourceProductionContext context, IncrementalGeneratorContext igContext)
    {

        try
        {
            RegisterSourceOutput(context, igContext);
        }
        catch (Exception exception) when (HandleException(context, exception))
        {
        }
    }

    protected abstract void RegisterSourceOutput(SourceProductionContext context, IncrementalGeneratorContext igContext);
    protected abstract IncrementalValueProvider<IncrementalGeneratorContext> Execute(IncrementalGeneratorInitializationContext context);
    protected abstract void AddSyntaxProvider(SyntaxValueProvider syntaxProvider);

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

            //if (acop.GlobalOptions.TryGetValue("build_property.orleans_idattributes", out var idAttributes) && idAttributes is { Length: > 0 })
            //{
            //    options.IdAttributes.AddRange(idAttributes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList());
            //}

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

}
